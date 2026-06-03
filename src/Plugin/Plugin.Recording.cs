using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace K4GOTV;

public sealed partial class Plugin
{
	private CancellationTokenSource? _idleTimerCts;

	private bool _isRecording;
	private string? _fileName;
	private string _currentMapName = "unknown";
	private double _demoStartTime;
	private DateTime _realStartTime; // Új változó a valós idő mérésére
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

		// Elmentjük a pálya nevét biztonságos helyre, amikor még él a motor
		_currentMapName = GetSafeMapName();

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
		_realStartTime = DateTime.UtcNow; // Itt indítjuk a valós stopperórát
		_lastPlayerCheckTime = _demoStartTime;

		Core.Logger.LogInformation("Recording started: {FileName} on map: {Map}", _fileName, _currentMapName);

		if (Config.CurrentValue.AutoRecord.StopOnIdle)
		{
			_idleTimerCts?.Cancel();
			_idleTimerCts = Core.Scheduler.RepeatBySeconds(1f, CheckIdleState);
		}
	}

	private void StopRecording(bool isMapUnload = false)
	{
		_idleTimerCts?.Cancel();
		_idleTimerCts = null;

		if (!_isRecording || string.IsNullOrEmpty(_fileName))
		{
			if (!isMapUnload) 
				ResetRecordingState();
			return;
		}

		var stoppedFileName = _fileName;
		var demoPath = Path.Combine(DemoDirectory, $"{stoppedFileName}.dem");

		// Biztonságos adatmentés helyi C# változókból
		double duration = 0;
		string mapName = _currentMapName;
		string serverName = "CS2 Server";
		int round = 1;
		int playerCount = _lastKnownPlayerCount;
		var requesters = new List<(string Name, ulong SteamId)>();

		if (isMapUnload)
		{
			Core.Logger.LogInformation("Map unload detected. Catching demo data safely from C# memory (Map: {Map}).", mapName);
			
			// Tűpontos időszámítás a két DateTime különbségéből, teljesen függetlenül a játékmotortól
			try 
			{ 
				duration = (DateTime.UtcNow - _realStartTime).TotalSeconds; 
			} 
			catch 
			{ 
				duration = 0; 
			}
		}
		else
		{
			var currentTime = GetSafeCurrentTime();
			duration = Math.Max(0, currentTime - _demoStartTime);
			requesters = _requesters.ToList();
			mapName = GetSafeMapName();
			serverName = GetSafeServerName();
			round = GetSafeRound();
			UpdateLastKnownPlayerCount();
			playerCount = Math.Max(_lastKnownPlayerCount, requesters.Count);

			try
			{
				Core.Engine.ExecuteCommand("tv_stoprecord");
				Core.Logger.LogInformation("Recording stopped normally: {FileName}", stoppedFileName);
			}
			catch (Exception ex)
			{
				Core.Logger.LogError("Failed to stop GOTV recording: {Message}", ex.Message);
			}
		}

		ResetRecordingState();

		// Háttérszál indítása a zippeléshez és feltöltéshez
		Task.Run(async () =>
		{
			try
			{
				if (isMapUnload) await Task.Delay(3000);

				var finalDemoPath = await WaitForDemoFileAsync(demoPath, TimeSpan.FromSeconds(20));

				if (finalDemoPath == null)
				{
					Core.Logger.LogError("Demo file (.dem) could not be verified on disk: {Path}", demoPath);
					return;
				}

				Core.Logger.LogInformation("Starting compression and upload for: {FileName}", stoppedFileName);
				
				await ProcessDemoAsync(
					stoppedFileName,
					finalDemoPath,
					requesters,
					TimeSpan.FromSeconds(duration > 0 ? duration : 300),
					round,
					playerCount,
					mapName,
					serverName
				);
			}
			catch (Exception ex)
			{
				Core.Logger.LogError("Error in background upload thread: {Message}", ex.Message);
			}
		});
	}

	public async Task<string?> WaitForDemoFileAsync(string expectedPath, TimeSpan timeout)
	{
		var startedAt = DateTime.UtcNow;

		while (DateTime.UtcNow - startedAt < timeout)
		{
			if (File.Exists(expectedPath))
			{
				var info = new FileInfo(expectedPath);
				if (info.Length > 0) return expectedPath;
			}

			if (File.Exists(expectedPath + ".dem"))
			{
				var info = new FileInfo(expectedPath + ".dem");
				if (info.Length > 0) return expectedPath + ".dem";
			}

			var directory = Path.GetDirectoryName(expectedPath);
			var baseName = Path.GetFileNameWithoutExtension(expectedPath);

			if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
			{
				var files = Directory.GetFiles(directory, $"{baseName}*.dem");
				if (files.Length > 0)
				{
					var file = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
					if (new FileInfo(file).Length > 0) return file;
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
		_currentMapName = "unknown";
		_demoStartTime = 0;
		_demoRequestedThisRound = false;
		_lastKnownPlayerCount = 0;
		
		try
		{
			_requesters.Clear();
		}
		catch
		{
			// Elnyelve
		}
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
