using System;
using System.Threading.Tasks;
using BAModAPI;
using BAModAPI.Services;
using UnityEngine;

[assembly: RegisterModClass(typeof(LogisticsTerminal.TerminalMod))]

namespace LogisticsTerminal;

// 进入城市（读档）时加载，退出到主菜单时卸载
[ModEntryOnCityLoad]
public class TerminalMod : ModBigAmbitionsBase
{
	public static string AppTitle => L.T("物流终端", "Logistics Terminal");

	private static ModContext _context;

	private static GameObject _host;

	public override Task OnLoadAsync(ModContext context)
	{
		_context = context;
		try
		{
			_host = new GameObject("LogisticsTerminal");
			UnityEngine.Object.DontDestroyOnLoad(_host);
			_host.AddComponent<TerminalWindow>();
			Ledger.Initialize();
			PhoneButtonInjector.Initialize();
			UnityLifecycleProvider.OnUpdate += OnUpdate;
			Log("Logistics Terminal loaded. Open it from the phone or press F8.");
		}
		catch (Exception ex)
		{
			LogError("Load failed: " + ex);
		}
		return Task.CompletedTask;
	}

	public override Task OnUnloadAsync()
	{
		try
		{
			UnityLifecycleProvider.OnUpdate -= OnUpdate;
			Ledger.Shutdown();
			PhoneButtonInjector.Remove();
			if (_host != null)
			{
				UnityEngine.Object.Destroy(_host);
			}
		}
		catch (Exception ex)
		{
			LogError("Unload failed: " + ex);
		}
		_host = null;
		_context = null;
		return Task.CompletedTask;
	}

	private static void OnUpdate()
	{
		try
		{
			PhoneButtonInjector.OnUpdate();
			if (Input.GetKeyDown(KeyCode.F8))
			{
				TerminalWindow.Toggle();
			}
		}
		catch (Exception ex)
		{
			LogWarning("OnUpdate: " + ex.Message);
		}
	}

	public static void Log(string message)
	{
		if (_context?.Logger != null) _context.Logger.Info(message);
		else Debug.Log("[LogisticsTerminal] " + message);
	}

	public static void LogWarning(string message)
	{
		if (_context?.Logger != null) _context.Logger.Warn(message);
		else Debug.LogWarning("[LogisticsTerminal] " + message);
	}

	public static void LogError(string message)
	{
		if (_context?.Logger != null) _context.Logger.Error(message);
		else Debug.LogError("[LogisticsTerminal] " + message);
	}
}
