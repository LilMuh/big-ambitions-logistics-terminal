using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace LogisticsTerminal;

// 工厂每天的生产和原料消耗，游戏没有记录。
// mod 在每天 0 点（游戏的 newday 事件，早于凌晨 2 点的物流配送）给每个工厂的库存拍一次快照，
// 两天快照的差值再加上当天的配送记录，就能算出这一天生产了多少、用了多少。
public static class Ledger
{
	private const string NewDayEvent = "ba:gameevent_newday";

	private const int KeepDays = 14;

	// 天 → 工厂 → 货物 → 当天 0 点的库存
	private static Dictionary<int, Dictionary<string, Dictionary<string, int>>> _snapshots =
		new Dictionary<int, Dictionary<string, Dictionary<string, int>>>();

	private static string _path;

	public static void Initialize()
	{
		_snapshots.Clear();
		_path = null;
		var save = SaveGameManager.Current;
		if (save != null)
		{
			string id = Sanitize((save.seed ?? "") + "_" + (save.characterId ?? ""));
			_path = Path.Combine(Application.persistentDataPath, "LogisticsTerminal", "ledger_" + id + ".json");
			Load();
		}
		GameEvent.onGameEventTriggered -= OnGameEvent;
		GameEvent.onGameEventTriggered += OnGameEvent;
	}

	public static void Shutdown()
	{
		GameEvent.onGameEventTriggered -= OnGameEvent;
	}

	public static string Key(BuildingRegistration registration)
	{
		return registration.StreetName + "|" + registration.StreetNumber;
	}

	// 这一天 0 点到第二天 0 点之间每种货物库存的变化。缺任一天的快照就返回 false。
	public static bool TryGetStockChange(BuildingRegistration registration, int day, out Dictionary<string, int> change)
	{
		change = null;
		string key = Key(registration);
		if (!_snapshots.TryGetValue(day, out var startDay) || !startDay.TryGetValue(key, out var start)) return false;
		if (!_snapshots.TryGetValue(day + 1, out var endDay) || !endDay.TryGetValue(key, out var end)) return false;
		change = new Dictionary<string, int>();
		foreach (string item in start.Keys.Union(end.Keys))
		{
			start.TryGetValue(item, out int a);
			end.TryGetValue(item, out int b);
			change[item] = b - a;
		}
		return true;
	}

	private static void OnGameEvent(string gameEvent)
	{
		if (gameEvent != NewDayEvent) return;
		try
		{
			TakeSnapshot();
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Ledger snapshot failed: " + ex.Message);
		}
	}

	private static void TakeSnapshot()
	{
		var save = SaveGameManager.Current;
		if (save == null) return;
		var today = new Dictionary<string, Dictionary<string, int>>();
		foreach (var factory in GameData.PlayerFactories())
		{
			var stock = new Dictionary<string, int>();
			foreach (string item in factory.GetProducts())
			{
				stock[item] = Helpers.BuildingHelper.CountResourcesInPallets(factory.Address, item);
			}
			today[Key(factory)] = stock;
		}
		_snapshots[save.Day] = today;
		foreach (int old in _snapshots.Keys.Where(d => d < save.Day - KeepDays || d > save.Day).ToList())
		{
			_snapshots.Remove(old);
		}
		Save();
	}

	private static void Load()
	{
		try
		{
			if (_path == null || !File.Exists(_path)) return;
			var loaded = JsonConvert.DeserializeObject<Dictionary<int, Dictionary<string, Dictionary<string, int>>>>(File.ReadAllText(_path));
			if (loaded != null) _snapshots = loaded;
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Failed to read ledger file: " + ex.Message);
		}
	}

	private static void Save()
	{
		try
		{
			if (_path == null) return;
			Directory.CreateDirectory(Path.GetDirectoryName(_path));
			File.WriteAllText(_path, JsonConvert.SerializeObject(_snapshots));
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Failed to save ledger file: " + ex.Message);
		}
	}

	private static string Sanitize(string text)
	{
		var chars = text.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
		return new string(chars);
	}
}
