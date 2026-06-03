using System.Text.Json;
using CG.Web.MegaApiClient;
using FluentFTP;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

public sealed partial class Plugin
{
	private record RetentionRecord(string Service, string Identifier, DateTime UploadedAt);

	// Szálbiztos zárolás a retenciós fájl írásához/olvasásához
	private static readonly SemaphoreSlim _retentionLock = new(1, 1);

	private async Task DeleteFileAsync(string path, bool forceDeleteDemo = false)
	{
		for (int i = 0; i < 3; i++)
		{
			try
			{
				if (!File.Exists(path))
					return;

				var extension = Path.GetExtension(path);

				if (extension.Equals(".dem", StringComparison.OrdinalIgnoreCase) && !forceDeleteDemo)
				{
					Core.Logger.LogInformation("Skipped demo delete: {Path}", path);
					return;
				}

				File.Delete(path);

				if (Config.CurrentValue.General.LogDeletions)
					Core.Logger.LogInformation("Deleted: {Path}", path);

				return;
			}
			catch (IOException) when (i < 2)
			{
				await Task.Delay(100 * (i + 1));
			}
			catch (Exception ex)
			{
				Core.Logger.LogError("Delete failed: {Message}", ex.Message);
				return;
			}
		}
	}

	private async Task DeleteEveryLocalDemoFileAfterServerStartAsync()
	{
		try
		{
			Directory.CreateDirectory(DemoDirectory);

			var files = Directory.GetFiles(DemoDirectory, "*.dem")
				.Concat(Directory.GetFiles(DemoDirectory, "*.zip"))
				.ToList();

			if (files.Count == 0)
			{
				Core.Logger.LogInformation("Server start demo cleanup: no .dem or .zip files found.");
				return;
			}

			Core.Logger.LogInformation("Server start demo cleanup started. Files: {Count}", files.Count);

			foreach (var file in files)
			{
				await DeleteFileAsync(file, forceDeleteDemo: true);
			}

			Core.Logger.LogInformation("Server start demo cleanup finished.");
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Server start demo cleanup failed: {Message}", ex.Message);
		}
	}

	private void CleanupOldFiles()
	{
		// A teljes merevlemez-olvasást kiszervezzük háttérszálra, hogy ne lagoltassa a játékot
		Task.Run(async () =>
		{
			try
			{
				if (!Directory.Exists(DemoDirectory)) return;

				var cutoff = DateTime.Now.AddHours(-Config.CurrentValue.General.AutoCleanupFileAgeHours);
				var files = Directory.GetFiles(DemoDirectory, "*.dem")
					.Concat(Directory.GetFiles(DemoDirectory, "*.zip"));

				foreach (var file in files)
				{
					if (File.GetCreationTime(file) < cutoff)
					{
						await DeleteFileAsync(file);
					}
				}
			}
			catch (Exception ex)
			{
				Core.Logger.LogError("Auto cleanup task failed: {Message}", ex.Message);
			}
		});
	}

	// Szálbiztos aszinkron IO műveletek a retenciós fájlhoz
	private async Task<List<RetentionRecord>> LoadRetentionRecordsAsync()
	{
		if (!File.Exists(RetentionFilePath))
			return [];

		await _retentionLock.WaitAsync();
		try
		{
			var json = await File.ReadAllTextAsync(RetentionFilePath);
			return JsonSerializer.Deserialize<List<RetentionRecord>>(json) ?? [];
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Failed to load retention records: {Message}", ex.Message);
			return [];
		}
		finally
		{
			_retentionLock.Release();
		}
	}

	private async Task SaveRetentionRecordsAsync(List<RetentionRecord> records)
	{
		await _retentionLock.WaitAsync();
		try
		{
			var dir = Path.GetDirectoryName(RetentionFilePath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			await File.WriteAllTextAsync(RetentionFilePath, JsonSerializer.Serialize(records));
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Failed to save retention records: {Message}", ex.Message);
		}
		finally
		{
			_retentionLock.Release();
		}
	}

	private async Task AddRetentionRecordAsync(string service, string identifier)
	{
		var records = await LoadRetentionRecordsAsync();
		records.Add(new RetentionRecord(service, identifier, DateTime.Now));
		await SaveRetentionRecordsAsync(records);
	}

	private async Task CleanFtpRetentionAsync()
	{
		if (!Config.CurrentValue.Ftp.RetentionEnabled)
			return;

		var records = await LoadRetentionRecordsAsync();
		var expired = records.Where(r => r.Service == "ftp" && (DateTime.Now - r.UploadedAt).TotalHours >= Config.CurrentValue.Ftp.RetentionHours).ToList();
		if (expired.Count == 0)
			return;

		var cfg = Config.CurrentValue.Ftp;
		using var client = new AsyncFtpClient(cfg.Host, cfg.Username, cfg.Password, cfg.Port);
		client.Config.EncryptionMode = cfg.UseSftp ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;
		client.Config.ValidateAnyCertificate = true;

		try
		{
			await client.AutoConnect();

			var removed = new List<RetentionRecord>();
			foreach (var record in expired)
			{
				try
				{
					await client.DeleteFile(record.Identifier);
					removed.Add(record);
					Core.Logger.LogInformation("Deleted FTP file: {Id}", record.Identifier);
				}
				catch (Exception ex)
				{
					// FIX: String interpolációra cserélve a CA2017-es logolási warning elkerülése érdekében
					Core.Logger.LogError($"FTP delete failed for {record.Identifier}: {ex.Message}");
				}
			}

			await client.Disconnect();
			
			if (removed.Count > 0) 
				await SaveRetentionRecordsAsync(records.Except(removed).ToList());
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("FTP retention connection failed: {Message}", ex.Message);
		}
	}

	private async Task CleanMegaRetentionAsync()
	{
		if (!Config.CurrentValue.Mega.RetentionEnabled)
			return;

		var records = await LoadRetentionRecordsAsync();
		var expired = records.Where(r => r.Service == "mega" && (DateTime.Now - r.UploadedAt).TotalHours >= Config.CurrentValue.Mega.RetentionHours).ToList();
		if (expired.Count == 0)
			return;

		var client = new MegaApiClient();
		try
		{
			await client.LoginAsync(Config.CurrentValue.Mega.Email, Config.CurrentValue.Mega.Password);
			var nodes = await client.GetNodesAsync();

			var removed = new List<RetentionRecord>();
			foreach (var record in expired)
			{
				try
				{
					var node = nodes.SingleOrDefault(n => n.Id.ToString() == record.Identifier);
					if (node != null)
					{
						await client.DeleteAsync(node, moveToTrash: false);
						Core.Logger.LogInformation("Deleted Mega node: {Id}", record.Identifier);
					}
					removed.Add(record);
				}
				catch (Exception ex)
				{
					Core.Logger.LogError("Mega delete failed for {Id}: {Message}", record.Identifier, ex.Message);
					// Ha a fájl már nem létezik Megán (pl. manuálisan törölték), akkor is vegyük ki a listából, ne próbálgassa örökké
					if (ex.Message.Contains("ResourceNotExists") || ex.Message.Contains("NodeNotFound"))
					{
						removed.Add(record);
					}
				}
			}

			if (removed.Count > 0) 
				await SaveRetentionRecordsAsync(records.Except(removed).ToList());
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Mega retention failed: {Message}", ex.Message);
		}
		finally
		{
			if (client.IsLoggedIn)
				await client.LogoutAsync();
		}
	}
}
