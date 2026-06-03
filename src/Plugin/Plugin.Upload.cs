using System.IO.Compression;
using System.Text;
using CG.Web.MegaApiClient;
using FluentFTP;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

public sealed partial class Plugin
{
	private async Task ProcessDemoAsync(
		string fileName,
		string demoPath,
		List<(string Name, ulong SteamId)> requesters,
		TimeSpan duration,
		int round,
		int playerCount,
		string mapName,
		string serverName)
	{
		var zipPath = Path.Combine(DemoDirectory, $"{fileName}.zip");

		try
		{
			if (!File.Exists(demoPath))
			{
				Core.Logger.LogError("Demo processing skipped, source demo does not exist: {Path}", demoPath);
				return;
			}

			var demoInfo = new FileInfo(demoPath);
			if (demoInfo.Length <= 0)
			{
				Core.Logger.LogError("Demo processing skipped, source demo is empty: {Path}", demoPath);
				return;
			}

			if (!await ZipFileAsync(demoPath, zipPath))
			{
				Core.Logger.LogError("Demo processing stopped because zip creation failed: {FileName}", fileName);
				return;
			}

			if (!File.Exists(zipPath))
			{
				Core.Logger.LogError("Zip file was not created: {Path}", zipPath);
				return;
			}

			var zipInfo = new FileInfo(zipPath);
			var fileSizeBytes = zipInfo.Length;

			string? megaLink = null;
			string? ftpLink = null;

			if (Config.CurrentValue.Ftp.Enabled)
			{
				if (string.IsNullOrWhiteSpace(Config.CurrentValue.Ftp.Host))
				{
					Core.Logger.LogError("FTP upload skipped: host is empty.");
				}
				else
				{
					var remotePath = Path.Combine(
						Config.CurrentValue.Ftp.RemoteDirectory,
						Path.GetFileName(zipPath)
					).Replace("\\", "/");

					ftpLink = await UploadToFtpAsync(zipPath, remotePath);

					if (!string.IsNullOrWhiteSpace(ftpLink))
					{
						if (Config.CurrentValue.Ftp.RetentionEnabled)
							await AddRetentionRecordAsync("ftp", remotePath);
					}
					else
					{
						Core.Logger.LogError("FTP upload failed or returned empty link.");
					}
				}
			}

			if (Config.CurrentValue.Mega.Enabled)
			{
				if (string.IsNullOrWhiteSpace(Config.CurrentValue.Mega.Email))
				{
					Core.Logger.LogError("Mega upload skipped: email is empty.");
				}
				else if (string.IsNullOrWhiteSpace(Config.CurrentValue.Mega.Password))
				{
					Core.Logger.LogError("Mega upload skipped: password is empty.");
				}
				else
				{
					var (link, nodeId) = await UploadToMegaAsync(zipPath);

					megaLink = link;

					if (!string.IsNullOrWhiteSpace(nodeId))
					{
						if (Config.CurrentValue.Mega.RetentionEnabled)
							await AddRetentionRecordAsync("mega", nodeId);
					}
					else
					{
						Core.Logger.LogError("Mega upload failed. Returned link: {Link}", megaLink);
					}
				}
			}

			// Discord webhook kiküldése a linkekkel
			await SendToDiscordAsync(
				fileName,
				zipPath,
				fileSizeBytes,
				megaLink,
				ftpLink,
				requesters,
				duration,
				round,
				playerCount,
				mapName,
				serverName
			);

			if (_database?.IsEnabled == true && (!string.IsNullOrWhiteSpace(megaLink) || !string.IsNullOrWhiteSpace(ftpLink)))
			{
				await _database.StoreDemoRecordAsync(
					fileName,
					megaLink,
					ftpLink,
					requesters,
					duration,
					round,
					playerCount,
					mapName,
					serverName
				);
			}

			// Nyers .dem fájl törlése
			if (Config.CurrentValue.General.DeleteDemoAfterUpload && (!string.IsNullOrWhiteSpace(megaLink) || !string.IsNullOrWhiteSpace(ftpLink)))
			{
				if (File.Exists(demoPath))
				{
					File.Delete(demoPath);
				}
			}

			// Tömörített .zip fájl törlése
			if (Config.CurrentValue.General.DeleteZippedDemoAfterUpload && (!string.IsNullOrWhiteSpace(megaLink) || !string.IsNullOrWhiteSpace(ftpLink)))
			{
				if (File.Exists(zipPath))
				{
					File.Delete(zipPath);
				}
			}

			// EZ AZ EGYETLEN ÉRTESÍTÉS MARADT: Jelzi, hogy a folyamat kész, és kiírja a közvetlen Mega linket a konzolba
			Core.Logger.LogInformation("Demo uploaded to Mega: {Link}", !string.IsNullOrWhiteSpace(megaLink) ? megaLink : "No Mega link generated");
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Demo processing failed: {Message}", ex.ToString());
		}
	}

	private async Task<bool> ZipFileAsync(string sourcePath, string zipPath)
	{
		try
		{
			if (!File.Exists(sourcePath))
			{
				Core.Logger.LogError("Zip failed, source file does not exist: {Path}", sourcePath);
				return false;
			}

			var sourceInfo = new FileInfo(sourcePath);
			if (sourceInfo.Length <= 0)
			{
				Core.Logger.LogError("Zip failed, source file is empty: {Path}", sourcePath);
				return false;
			}

			if (File.Exists(zipPath))
			{
				File.Delete(zipPath);
			}

			await Task.Run(() =>
			{
				using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
				zip.CreateEntryFromFile(
					sourcePath,
					Path.GetFileName(sourcePath),
					CompressionLevel.Optimal
				);
			});

			if (!File.Exists(zipPath))
			{
				Core.Logger.LogError("Zip failed, zip file does not exist after creation: {Path}", zipPath);
				return false;
			}

			var zipInfo = new FileInfo(zipPath);
			if (zipInfo.Length <= 0)
			{
				Core.Logger.LogError("Zip failed, created zip is empty: {Path}", zipPath);
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Zip failed: {Message}", ex.ToString());
			return false;
		}
	}

	private async Task<string?> UploadToFtpAsync(string filePath, string remotePath)
	{
		var cfg = Config.CurrentValue.Ftp;
		using var client = new AsyncFtpClient(cfg.Host, cfg.Username, cfg.Password, cfg.Port);

		try
		{
			client.Config.EncryptionMode = cfg.UseSftp ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;
			client.Config.ValidateAnyCertificate = true;

			await client.AutoConnect();
			await client.UploadFile(filePath, remotePath);

			var protocol = cfg.UseSftp ? "sftp" : "ftp";
			return $"{protocol}://{cfg.Host}/{remotePath.TrimStart('/')}";
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("FTP upload error: {Message}", ex.ToString());
			return null;
		}
		finally
		{
			try { await client.Disconnect(); } catch { }
		}
	}

	private async Task<(string? Link, string? NodeId)> UploadToMegaAsync(string filePath)
	{
		var client = new MegaApiClient();

		try
		{
			if (!File.Exists(filePath))
			{
				Core.Logger.LogError("Mega upload skipped, file does not exist: {Path}", filePath);
				return (null, null);
			}

			var fileInfo = new FileInfo(filePath);
			if (fileInfo.Length <= 0)
			{
				Core.Logger.LogError("Mega upload skipped, file is empty: {Path}", filePath);
				return (null, null);
			}

			await client.LoginAsync(
				Config.CurrentValue.Mega.Email,
				Config.CurrentValue.Mega.Password
			);

			var nodes = await client.GetNodesAsync();
			var rootNode = nodes.Single(x => x.Type == NodeType.Root);

			var uploadedNode = await client.UploadFileAsync(filePath, rootNode);
			var downloadLink = await client.GetDownloadLinkAsync(uploadedNode);

			return (downloadLink.ToString(), uploadedNode.Id.ToString());
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Mega upload error: {Message}", ex.ToString());
			return (null, null);
		}
		finally
		{
			if (client.IsLoggedIn)
			{
				try { await client.LogoutAsync(); } catch { }
			}
		}
	}

	private async Task SendToDiscordAsync(
		string fileName,
		string zipPath,
		long fileSizeBytes,
		string? megaLink,
		string? ftpLink,
		List<(string Name, ulong SteamId)> requesters,
		TimeSpan duration,
		int round,
		int playerCount,
		string mapName,
		string serverName)
	{
		if (string.IsNullOrWhiteSpace(Config.CurrentValue.Discord.WebhookURL))
			return;

		if (!File.Exists(PayloadTemplatePath))
		{
			Core.Logger.LogError("Payload template not found: {Path}", PayloadTemplatePath);
			return;
		}

		var template = await File.ReadAllTextAsync(PayloadTemplatePath);
		var fileSizeMB = fileSizeBytes / (1024 * 1024);

		var safeMegaLink = !string.IsNullOrWhiteSpace(megaLink) ? megaLink : "Nincs Mega link";
		var safeFtpLink = !string.IsNullOrWhiteSpace(ftpLink) ? ftpLink : "Nincs FTP link";

		var downloadLinks = new List<string>();

		if (!string.IsNullOrWhiteSpace(megaLink))
			downloadLinks.Add($"**Mega:** [Kattints ide a demó fájl letöltéséhez]({megaLink})");

		if (!string.IsNullOrWhiteSpace(ftpLink))
			downloadLinks.Add($"**FTP:** [Kattints ide a demó fájl letöltéséhez]({ftpLink})");

		var payload = template
			.Replace("{webhook_name}", Config.CurrentValue.Discord.WebhookName)
			.Replace("{webhook_avatar}", Config.CurrentValue.Discord.WebhookAvatar)
			.Replace("{message_text}", Config.CurrentValue.Discord.MessageText)
			.Replace("{embed_title}", Config.CurrentValue.Discord.EmbedTitle)
			.Replace("{map}", mapName)
			.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
			.Replace("{time}", DateTime.Now.ToString("HH:mm:ss"))
			.Replace("{timedate}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
			.Replace("{length}", duration.ToString(@"mm\:ss"))
			.Replace("{round}", round.ToString())
			.Replace("{download_links}", downloadLinks.Count > 0 ? string.Join("\\n", downloadLinks) : "No external uploads")
			.Replace("{mega_link}", safeMegaLink)
			.Replace("{megaLink}", safeMegaLink)
			.Replace("{MEGA_LINK}", safeMegaLink)
			.Replace("{ftp_link}", safeFtpLink)
			.Replace("{ftpLink}", safeFtpLink)
			.Replace("{FTP_LINK}", safeFtpLink)
			.Replace("{sftp_link}", safeFtpLink)
			.Replace("{sftpLink}", safeFtpLink)
			.Replace("{SFTP_LINK}", safeFtpLink)
			.Replace("{requester_name}", string.Join(", ", requesters.Select(r => r.Name)))
			.Replace("{requester_steamid}", string.Join(", ", requesters.Select(r => r.SteamId)))
			.Replace("{requester_both}", string.Join("\\n", requesters.Select(r => $"{r.Name} ({r.SteamId})")))
			.Replace("{requester_count}", requesters.Count.ToString())
			.Replace("{player_count}", playerCount.ToString())
			.Replace("{server_name}", serverName)
			.Replace("{fileName}", fileName)
			.Replace("{iso_timestamp}", DateTime.UtcNow.ToString("o"))
			.Replace("{file_size_warning}", fileSizeMB > MaxDiscordFileSizeMB ? $"⚠️ File size ({fileSizeMB}MB) exceeds Discord limit." : "")
			.Replace("{fileSizeInKB}", (fileSizeBytes / 1024).ToString());

		using var httpClient = new HttpClient();
		using var content = new MultipartFormDataContent
		{
			{ new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json" }
		};

		if (File.Exists(zipPath) && fileSizeMB <= MaxDiscordFileSizeMB && Config.CurrentValue.Discord.WebhookUploadFile)
		{
			content.Add(
				new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)),
				"file",
				$"{fileName}.zip"
			);
		}

		var response = await httpClient.PostAsync(Config.CurrentValue.Discord.WebhookURL, content);
		response.EnsureSuccessStatusCode();
	}
}
