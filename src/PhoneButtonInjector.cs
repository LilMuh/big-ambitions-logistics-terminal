using System;
using Localizor.LanguageChangeEvent;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LogisticsTerminal;

// 在游戏手机主屏上克隆一个已有的 App 按钮，改成「物流终端」
public static class PhoneButtonInjector
{
	private const string ButtonName = "LogisticsTerminalApp";

	// 手机 UI 在读档后才会生成，按帧间隔重试
	private const int SearchIntervalFrames = 60;

	private const int MaxRetries = 60;

	private static GameObject _button;

	private static int _cooldown;

	private static int _retries;

	private static Sprite _icon;

	public static void Initialize()
	{
		_button = null;
		_cooldown = 0;
		_retries = 0;
	}

	public static void OnUpdate()
	{
		// 按钮被游戏销毁（比如重新加载 UI）后重新注入
		if (_button != null || _retries >= MaxRetries)
		{
			return;
		}
		if (--_cooldown > 0)
		{
			return;
		}
		_cooldown = SearchIntervalFrames;
		_retries++;
		TryInject();
	}

	public static void Remove()
	{
		if (_button != null)
		{
			Object.Destroy(_button);
		}
		_button = null;
	}

	private static void TryInject()
	{
		try
		{
			SmartphoneUI phone = Object.FindObjectOfType<SmartphoneUI>();
			if (phone == null)
			{
				var all = Resources.FindObjectsOfTypeAll<SmartphoneUI>();
				phone = all.Length > 0 ? all[0] : null;
			}
			if (phone == null)
			{
				return;
			}

			SmartphoneAppButton template = null;
			foreach (SmartphoneAppButton candidate in phone.appButtons.Values)
			{
				if (candidate != null)
				{
					template = candidate;
					break;
				}
			}
			if (template == null)
			{
				return;
			}

			Transform parent = template.transform.parent;
			Transform existing = parent.Find(ButtonName);
			if (existing != null)
			{
				_button = existing.gameObject;
				return;
			}

			GameObject clone = Object.Instantiate(template.gameObject, parent);
			clone.name = ButtonName;
			clone.SetActive(true);
			clone.transform.SetAsLastSibling();

			var appButton = clone.GetComponent<SmartphoneAppButton>();
			if (appButton != null)
			{
				TrySafe(() => appButton.UpdateBadgeCount(0));
				TrySafe(appButton.HideOutline);
				TrySafe(() => appButton.SetIcon(GetIcon()));
			}
			SetTitle(clone, TerminalMod.AppTitle);

			Button button = clone.GetComponent<Button>() ?? clone.GetComponentInChildren<Button>(true);
			if (button != null)
			{
				// 克隆会带上原 App 的点击回调，必须清掉
				button.onClick.RemoveAllListeners();
				button.onClick.AddListener(TerminalWindow.Open);
			}

			_button = clone;
			TerminalMod.Log("Added to the phone.");
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Failed to add phone button: " + ex.Message);
		}
	}

	private static void SetTitle(GameObject root, string title)
	{
		foreach (var localized in root.GetComponentsInChildren<TextLocalizationComponent>(true))
		{
			TrySafe(() => localized.SetValue(title, clearKey: true));
		}
		foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
		{
			TrySafe(() => text.text = title);
		}
	}

	// 代码生成的图标：深蓝底 + 三层货架
	private static Sprite GetIcon()
	{
		if (_icon != null)
		{
			return _icon;
		}
		const int size = 128;
		var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
		var background = new Color(0.12f, 0.33f, 0.62f, 1f);
		var shelf = new Color(0.95f, 0.95f, 0.95f, 1f);
		var box = new Color(0.98f, 0.72f, 0.2f, 1f);
		var pixels = new Color[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color c = background;
				bool inFrame = x >= 24 && x < 104;
				for (int level = 0; level < 3; level++)
				{
					int shelfY = 26 + level * 28;
					if (inFrame && y >= shelfY && y < shelfY + 5)
					{
						c = shelf;
					}
					// 每层上面放两个箱子
					bool onShelf = y >= shelfY + 5 && y < shelfY + 22;
					if (onShelf && ((x >= 30 && x < 58) || (x >= 66 && x < 94 && level != 1)))
					{
						c = box;
					}
				}
				if ((x >= 24 && x < 29 || x >= 99 && x < 104) && y >= 26 && y < 108)
				{
					c = shelf;
				}
				pixels[y * size + x] = c;
			}
		}
		texture.SetPixels(pixels);
		texture.Apply();
		_icon = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
		return _icon;
	}

	private static void TrySafe(Action action)
	{
		try
		{
			action();
		}
		catch
		{
		}
	}
}
