using System;
using Localizor;

namespace LogisticsTerminal;

// 界面文字：游戏语言是中文时显示中文，其它语言显示英文
public static class L
{
	public static bool Chinese
	{
		get
		{
			try
			{
				string locale = LocalizorManager.LoadedLocale ?? "";
				return locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
					|| locale.IndexOf("chinese", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			catch
			{
				return false;
			}
		}
	}

	public static string T(string zh, string en) => Chinese ? zh : en;

	// 带参数的句子，中英文语序不同时用：F("第 {0} 天", "Day {0}", day)
	public static string F(string zh, string en, params object[] args) => string.Format(T(zh, en), args);

	// 列表分隔符
	public static string Sep => T("、", ", ");
}
