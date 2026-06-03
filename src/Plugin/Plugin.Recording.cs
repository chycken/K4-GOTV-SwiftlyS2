using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace K4GOTV;

public sealed partial class Plugin
{
	private CancellationTokenSource? _idleTimerCts;

	private bool _isRecording;
	private string? _fileName;
	private double _demoStartTime;
	private double _lastPlayerCheckTime;
	private bool _demoRequestedThisRound;
	private int _lastKnownPlayerCount;
	private readonly List<(string Name, ulong SteamId)> _requesters = [];

	private void StartRecording(string baseName)
	{
		if (_isRecording)
			return;

		var gameRules = Core.EntitySystem.GetGameRules();
		if (!Config.CurrentValue.AutoRecord.RecordWarmup && gameRules?.WarmupPeriod == true)
			return;

		var pattern = Config.CurrentValue.AutoRecord.CropRounds
			? Config.CurrentValue.General.CropRoundsFileNamingPattern
			: Config.CurrentValue.General.RegularFileNamingPattern;

		_fileName = BuildFileName(pattern, baseName);
		var fullPath = Path.Combine(DemoDirectory, $"{_fileName}.dem");

		var counter = 1;
		while (File.Exists(fullPath))
		{
			_fileName = $"{_fileName}_{counter++}";
			fullPath = Path.Combine(DemoDirectory, $"{_fileName}.dem");
		}

		_lastKnownPlayerCount = Math.Max(1, GetSafePlayerCount());

		Core.Engine.ExecuteCommand($"tv_record \"{fullPath}\"");

		_isRecording = true;
		_demoStartTime = GetSafeCurrentTime();
		_lastPlayerCheckTime = _demoStartTime;

		Core.Logger.LogInformation("Recording started: {FileName}", _fileName);

		if (Config.CurrentValue.AutoRecord.StopOnIdle)
		{
			_idleTimerCts?.Cancel();
			_idleTimerCts = Core.Scheduler.RepeatBySeconds(1f, CheckIdleState);
		}
	}

	public void StopRecording(bool isMapUnload = false)
	{
		_idleTimerCts?.Cancel();
		_idleTimerCts = null;

		// Ha már nem rögzítünk, vagy nincs fájlnév, azonnal lépjünk ki
		if (!_isRecording || string.IsNullOrEmpty(_fileName))
		{
			ResetRecordingState();
			return;
		}

		// FIX: Térképváltáskor (Map Unload) a CS2 motor automatikusan lezárja a demót.
		// Ha ilyenkor manuálisan is ráküldjük a tv_stoprecord parancsot, a Source 2 motor hajlamos azonnal összeomlani (Segmentation fault).
		if (isMapUnload)
		{
			Core.Logger.LogInformation("Map unload detected. Skipping tv_stoprecord to let the engine close the file safely.");
			ResetRecordingState();
			return;
		}

		var stoppedFileName = _fileName;
		var demoPath = Path.Combine(DemoDirectory, $"{stoppedFileName}.dem");

		// Mindent a főszálon kérünk le, amíg az engine elérhető és stabil
		var currentTime = GetSafeCurrentTime();
		var duration = Math.Max(0, currentTime - _demoStartTime);
		var requesters = _requesters.ToList();

		var mapName = GetSafeMapName();
		var serverName = GetSafeServerName();
		var round = GetSafeRound();

		UpdateLastKnownPlayerCount();
		var playerCount = Math.Max(_lastKnownPlayerCount, requesters.Count);

		try
		{
			Core.Engine.ExecuteCommand("tv_stoprecord");
			Core.Logger.LogInformation("Recording stopped: {FileName}", stoppedFileName);
		}
		catch (Exception ex)
		{
			Core.Logger.LogError("Failed to stop GOTV recording: {Message}", ex.Message);
		}

		ResetRecordingState();

		if (duration < Config.CurrentValue.General.MinimumDemoDuration)
		{
			Core.Logger.LogInformation(
				"Demo skipped because duration is too short: {FileName}, {Duration}s",
				stoppedFileName,
				duration
			);
			return;
		}

		// A háttérszál biztonságos indítása, kizárólag előre kimentett, szálbiztos adatokkal
		Task.Run(async () =>
		{
			var finalDemoPath = await WaitForDemoFileAsync(demoPath, TimeSpan.FromSeconds(30));

			if (finalDemoPath == null)
			{
				Core.Logger.LogError("Demo file not found after waiting: {Path}", demoPath);
				try
				{
					if (Directory.Exists(DemoDirectory))
					{
						var files = Directory.GetFiles(DemoDirectory, "*.dem")
							.Select(Path.GetFileName);

						Core.Logger.LogInformation("Existing demo files: {Files}", string.Join(", ", files));
					}
				}
				catch (Exception ex)
				{
					Core.Logger.LogError("Failed to list demo directory: {Message}", ex.Message);
				}
				return;
			}

			await ProcessDemoAsync(
				stoppedFileName,
				finalDemoPath,
				requesters,
				TimeSpan.FromSeconds(duration),
				round,
				playerCount,
				mapName,
				serverName
			);
		});
	}

	private async Task<string?> WaitForDemoFileAsync(string expectedPath, TimeSpan timeout)
	{
		var startedAt = DateTime.UtcNow;

		string? lastPath = null;
		long lastSize = -1;
		int stableChecks = 0;

		while (DateTime.UtcNow - startedAt < timeout)
		{
			var candidates = new List<string>();

			if (File.Exists(expectedPath))
				candidates.Add(expectedPath);

			if (File.Exists(expectedPath + ".dem"))
				candidates.Add(expectedPath + ".dem");

			var directory = Path.GetDirectoryName(expectedPath);
			var baseName = Path.GetFileNameWithoutExtension(expectedPath);

			if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
			{
				candidates.AddRange(Directory.GetFiles(directory, $"{baseName}*.dem"));
			}

			var foundPath = candidates
				.Distinct()
				.Where(File.Exists)
				.Select(path => new FileInfo(path))
				.Where(info => info.Length > 0)
				.OrderByDescending(info => info.LastWriteTimeUtc)
				.Select(info => info.FullName)
				.FirstOrDefault();

			if (foundPath != null)
			{
				var currentSize = new FileInfo(foundPath).Length;

				if (foundPath == lastPath && currentSize == lastSize)
				{
					stableChecks++;

					if (stableChecks >= 2)
					{
						Core.Logger.LogInformation(
							"Demo file ready: {Path} ({Size} bytes)",
							foundPath,
							currentSize
						);

						return foundPath;
					}
				}
				else
				{
					lastPath = foundPath;
					lastSize = currentSize;
					stableChecks = 0;

					Core.Logger.LogInformation(
						"Waiting for demo file to stabilize: {Path} ({Size} bytes)",
						foundPath,
						currentSize
					);
				}
			}

			await Task.Delay(1000);
		}

		return null;
	}

	private void ResetRecordingState()
	{
		_isRecording = false;
		_fileName = null;
		_demoStartTime = 0;
		_demoRequestedThisRound = false;
		_lastKnownPlayerCount = 0;
	}

	private void CheckIdleState()
	{
		if (!_isRecording)
			return;

		UpdateLastKnownPlayerCount();

		var playerCount = GetSafePlayerCount();

		if (playerCount < Config.CurrentValue.AutoRecord.IdlePlayerCountThreshold)
		{
			var idleTime = GetSafeCurrentTime() - _lastPlayerCheckTime;

			if (idleTime > Config.CurrentValue.AutoRecord.IdleTimeSeconds)
			{
				Core.Logger.LogInformation("Stopping recording due to idle.");
				StopRecording();
			}
		}
		else
		{
			_lastPlayerCheckTime = GetSafeCurrentTime();
		}
	}

	private void UpdateLastKnownPlayerCount()
	{
		var playerCount = GetSafePlayerCount();

		if (playerCount > _lastKnownPlayerCount)
			_lastKnownPlayerCount = playerCount;
	}

	private string BuildFileName(string pattern, string baseName)
	{
		return pattern
			.Replace("{fileName}", baseName)
			.Replace("{map}", GetSafeMapName())
			.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
			.Replace("{time}", DateTime.Now.ToString("HH-mm-ss"))
			.Replace("{timestamp}", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
			.Replace("{round}", GetSafeRound().ToString())
			.Replace("{playerCount}", GetSafePlayerCount().ToString());
	}

	private double GetSafeCurrentTime()
	{
		try
		{
			return Core.Engine.GlobalVars.CurrentTime;
		}
		catch
		{
			return _demoStartTime;
		}
	}

	private string GetSafeMapName()
	{
		try
		{
			var mapName = Core.Engine.GlobalVars.MapName.ToString();
			return string.IsNullOrWhiteSpace(mapName) ? "unknown" : mapName;
		}
		catch
		{
			return "unknown";
		}
	}

	private string GetSafeServerName()
	{
		try
		{
			var hostname = Core.ConVar.Find<string>("hostname")?.Value?.ToString();
			return string.IsNullOrWhiteSpace(hostname) ? "Unknown Server" : hostname;
		}
		catch
		{
			return "Unknown Server";
		}
	}

	private int GetSafeRound()
	{
		try
		{
			var gameRules = Core.EntitySystem.GetGameRules();
			return (gameRules?.TotalRoundsPlayed ?? 0) + 1;
		}
		catch
		{
			return 0;
		}
	}

	private int GetSafePlayerCount()
	{
		try
		{
			return GetRealPlayerCount();
		}
		catch
		{
			return 0;
		}
	}
}
