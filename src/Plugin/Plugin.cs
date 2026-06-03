using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace K4GOTV;

[PluginMetadata(
	Id = "k4.gotv",
	Version = "1.0.3FIX",
	Name = "K4 - GOTV",
	Author = "K4ryuu+AI",
	Description = "Advanced GOTV handler with Discord, database, FTP, SFTP and Mega integration.")]
public sealed partial class Plugin(ISwiftlyCore core) : BasePlugin(core)
{
	private const string ConfigFileName = "k4-gotv.jsonc";
	private const string ConfigSection = "K4GOTV";

	public static IOptionsMonitor<PluginConfig> Config { get; private set; } = null!;

	private DatabaseService? _database;

	private CancellationTokenSource? _cleanupTimerCts;
	private CancellationTokenSource? _ftpRetentionTimerCts;
	private CancellationTokenSource? _megaRetentionTimerCts;

	private string DemoDirectory => Path.Combine(Core.CSGODirectory, Config.CurrentValue.General.DemoDirectory);
	private string RetentionFilePath => Path.Combine(Core.PluginDataDirectory, "uploads_retention.json");
	private string PayloadTemplatePath => Path.Combine(Core.PluginPath, "resources", "payload.json");

	private static int MaxDiscordFileSizeMB => Config.CurrentValue.Discord.ServerBoost switch { 2 => 50, 3 => 100, _ => 25 };

	public override void Load(bool hotReload)
	{
		Core.Configuration
			.InitializeJsonWithModel<PluginConfig>(ConfigFileName, ConfigSection)
			.Configure(builder =>
			{
				builder.AddJsonFile(ConfigFileName, optional: false, reloadOnChange: true);
			});

		ServiceCollection services = new();
		services.AddSwiftly(Core)
			.AddOptionsWithValidateOnStart<PluginConfig>()
			.BindConfiguration(ConfigSection);

		var provider = services.BuildServiceProvider();
		Config = provider.GetRequiredService<IOptionsMonitor<PluginConfig>>();

		Directory.CreateDirectory(DemoDirectory);
		Core.Logger.LogInformation("Demo directory: {Path} (exists: {Exists})", DemoDirectory, Directory.Exists(DemoDirectory));

		if (!hotReload && Config.CurrentValue.General.DeleteEveryDemoFromServerAfterServerStart)
		{
			Task.Run(DeleteEveryLocalDemoFileAfterServerStartAsync);
		}

		InitializeDatabase();
		RegisterEvents();
		RegisterCommands();
		StartTimers();

		if (hotReload && Config.CurrentValue.AutoRecord.Enabled && GetRealPlayerCount() > 0)
			StartRecording("autodemo");

		// --- JAVÍTÁS: FELDOLGOZATLAN DEMÓK RECOVERY INDÍTÁSA ÚJ PÁLYÁN ---
		Task.Run(async () =>
		{
			try
			{
				// Várunk 5 másodpercet, hogy a CS2 motor és a fájlrendszer megnyugodjon
				await Task.Delay(5000);

				if (!Directory.Exists(DemoDirectory)) return;

				var pendingFiles = Directory.GetFiles(DemoDirectory, "*.pending");
				foreach (var file in pendingFiles)
				{
					Core.Logger.LogInformation("Found unfinished demo token from the previous map: {File}", Path.GetFileName(file));

					try
					{
						var content = await File.ReadAllTextAsync(file);
						var parts = content.Split(';');

						if (parts.Length >= 7)
						{
							string pendingFileName = parts[0];
							string pendingDemoPath = parts[1];
							double pendingDuration = double.Parse(parts[2]);
							int pendingRound = int.Parse(parts[3]);
							int pendingPlayerCount = int.Parse(parts[4]);
							string pendingMapName = parts[5];
							string pendingServerName = parts[6];

							// Töröljük a pending tokent, nehogy hiba esetén végtelen ciklusba fusson
							File.Delete(file);

							var finalPath = await WaitForDemoFileAsync(pendingDemoPath, TimeSpan.FromSeconds(15));
							if (finalPath != null)
							{
								Core.Logger.LogInformation("Recovered previous demo successfully. Starting post-processing tasks...");
								
								await ProcessDemoAsync(
									pendingFileName,
									finalPath,
									new List<(string Name, ulong SteamId)>(), 
									TimeSpan.FromSeconds(pendingDuration),
									pendingRound,
									pendingPlayerCount,
									pendingMapName,
									pendingServerName
								);
							}
							else
							{
								Core.Logger.LogError("Could not locate physical demo file for: {Path}", pendingDemoPath);
							}
						}
					}
					catch (Exception ex)
					{
						Core.Logger.LogError("Error processing pending demo file {File}: {Message}", Path.GetFileName(file), ex.Message);
					}
				}
			}
			catch (Exception ex)
			{
				Core.Logger.LogError("Error in recovery monitor: {Message}", ex.Message);
			}
		});
	}

	public override void Unload()
	{
		// Átadjuk a true-t, jelezve, hogy a szerver leállása/pályaváltás váltotta ki az Unload-ot
		StopRecording(isMapUnload: true);

		_cleanupTimerCts?.Cancel();
		_ftpRetentionTimerCts?.Cancel();
		_megaRetentionTimerCts?.Cancel();
	}

	private void InitializeDatabase()
	{
		if (!string.IsNullOrEmpty(Config.CurrentValue.DatabaseConnection))
		{
			_database = new DatabaseService(Core, Config.CurrentValue.DatabaseConnection);
			Task.Run(_database.InitializeAsync);
		}
	}

	private void StartTimers()
	{
		if (Config.CurrentValue.General.AutoCleanupEnabled)
			_cleanupTimerCts = Core.Scheduler.RepeatBySeconds(Config.CurrentValue.General.AutoCleanupIntervalMinutes * 60f, () => Task.Run(CleanupOldFiles));

		if (Config.CurrentValue.Ftp.RetentionEnabled)
			_ftpRetentionTimerCts = Core.Scheduler.RepeatBySeconds(3600f, () => Task.Run(CleanFtpRetentionAsync));

		if (Config.CurrentValue.Mega.RetentionEnabled)
			_megaRetentionTimerCts = Core.Scheduler.RepeatBySeconds(3600f, () => Task.Run(CleanMegaRetentionAsync));
	}

	private int GetRealPlayerCount() =>
		Core.PlayerManager.GetAllPlayers().Count(p => p.IsValid && !p.IsFakeClient && p.Controller?.IsHLTV != true);
}
