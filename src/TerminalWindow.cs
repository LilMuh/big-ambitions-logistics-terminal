using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace LogisticsTerminal;

// 物流终端窗口。用 IMGUI 绘制，背后放一层透明 uGUI 遮罩，
// 让游戏的 EventSystem.IsPointerOverGameObject() 为真，避免点击/滚轮穿透到游戏世界。
// 尺寸按 1080p 设计，再乘以屏幕缩放直接用像素绘制（不用 GUI.matrix 拉伸，字才清楚）。
public class TerminalWindow : MonoBehaviour
{
	private enum View
	{
		All,
		Warehouse,
		Factory,
		Store,
	}

	// 表格的一格：width 为 0 表示平分剩余宽度
	private readonly struct Cell
	{
		public readonly string Text;
		public readonly GUIStyle Style;
		public readonly float Width;

		public Cell(string text, GUIStyle style, float width)
		{
			Text = text;
			Style = style;
			Width = width;
		}
	}

	private const float ReferenceHeight = 1080f;

	private const float AutoRefreshSeconds = 5f;

	private const int LowStockDays = 3;

	private const float SidebarWidth = 280f;

	private static readonly string[] FontCandidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" };

	private static readonly Color TextColor = new Color(0.12f, 0.14f, 0.18f);
	private static readonly Color MutedColor = new Color(0.36f, 0.39f, 0.44f);
	private static readonly Color WarnColor = new Color(0.78f, 0.16f, 0.16f);
	private static readonly Color GoodColor = new Color(0.1f, 0.48f, 0.24f);
	private static readonly Color OrangeColor = new Color(0.7f, 0.33f, 0.04f);
	private static readonly Color BlueColor = new Color(0.12f, 0.33f, 0.62f);
	private static readonly Color LightColor = new Color(0.9f, 0.92f, 0.95f);

	private static TerminalWindow _instance;

	private bool _visible;

	private Action _deferred;

	private TerminalData _data;

	private float _nextRefreshTime;

	private View _view = View.All;

	private int _index;

	private string _tab = "stock";

	// 确认框：改合同、改补货目标之前先列出会改什么
	private string _confirmTitle;
	private List<string> _confirmLines;
	private List<string> _confirmProblems;
	private string _confirmButton;
	private Func<string> _confirmAction;
	private Vector2 _confirmScroll;

	private string _message;
	private bool _messageGood;

	private Vector2 _sidebarScroll;

	private Vector2 _contentScroll;

	private GameObject _blocker;

	private Font _font;

	// 屏幕像素 / 设计像素
	private float _s = 1f;

	private float _stylesScale = -1f;

	// 表格可用宽度（像素，已扣掉滚动条）
	private float _tableWidth;

	private GUIStyle _window;
	private GUIStyle _title;
	private GUIStyle _h1;
	private GUIStyle _h2;
	private GUIStyle _header;
	private GUIStyle _headerRight;
	private GUIStyle _cell;
	private GUIStyle _cellWrap;
	private GUIStyle _cellBold;
	private GUIStyle _cellRight;
	private GUIStyle _cellRightBold;
	private GUIStyle _good;
	private GUIStyle _goodRight;
	private GUIStyle _warn;
	private GUIStyle _warnRight;
	private GUIStyle _orangeRight;
	private GUIStyle _mutedRight;
	private GUIStyle _muted;
	private GUIStyle _plainButton;
	private GUIStyle _sideButton;
	private GUIStyle _sideButtonSelected;
	private GUIStyle _tabButton;
	private GUIStyle _tabButtonSelected;
	private GUIStyle _badgeGood;
	private GUIStyle _badgeGray;
	private GUIStyle _badgeWarn;
	private GUIStyle _redButton;
	private GUIStyle _blueButton;
	private GUIStyle _card;
	private GUIStyle _tagImport;
	private GUIStyle _tagTransfer;
	private GUIStyle _tileLabel;
	private GUIStyle _tileValue;
	private GUIStyle _line;

	public static void Open()
	{
		if (_instance != null) _instance.SetVisible(true);
	}

	public static void Toggle()
	{
		if (_instance != null) _instance.SetVisible(!_instance._visible);
	}

	private void Awake()
	{
		_instance = this;
	}

	private void OnDestroy()
	{
		if (_blocker != null) Destroy(_blocker);
		if (_instance == this) _instance = null;
	}

	private void SetVisible(bool visible)
	{
		_visible = visible;
		if (visible)
		{
			Refresh();
		}
		else
		{
			CloseConfirm();
			_message = null;
		}
		EnsureBlocker();
		_blocker.SetActive(visible);
	}

	private void Refresh()
	{
		try
		{
			_data = TerminalData.Capture();
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Refresh failed: " + ex);
		}
		_nextRefreshTime = Time.unscaledTime + AutoRefreshSeconds;
	}

	// 按钮的动作放到下一帧的 Update 里做，避免 OnGUI 的布局和绘制两遍之间控件数量不一致
	private void Defer(Action action)
	{
		_deferred += action;
	}

	private void Update()
	{
		if (_deferred != null)
		{
			var actions = _deferred;
			_deferred = null;
			try
			{
				actions();
			}
			catch (Exception ex)
			{
				TerminalMod.LogWarning("Action failed: " + ex);
			}
		}
		if (!_visible) return;
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			SetVisible(false);
			return;
		}
		// 确认框开着的时候不自动刷新，免得列表在眼前变掉
		if (_confirmAction == null && Time.unscaledTime >= _nextRefreshTime) Refresh();
	}

	private void EnsureBlocker()
	{
		if (_blocker != null) return;
		_blocker = new GameObject("LogisticsTerminalInputBlocker");
		DontDestroyOnLoad(_blocker);
		var canvas = _blocker.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 30000;
		_blocker.AddComponent<GraphicRaycaster>();
		var image = new GameObject("Blocker").AddComponent<Image>();
		image.transform.SetParent(_blocker.transform, false);
		image.color = new Color(0f, 0f, 0f, 0.35f);
		image.raycastTarget = true;
		var rect = image.rectTransform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
		_blocker.SetActive(false);
	}

	private void OnGUI()
	{
		if (!_visible) return;
		_s = Mathf.Max(0.5f, Screen.height / ReferenceHeight);
		EnsureStyles();
		if (_font != null) GUI.skin.font = _font;

		var area = new Rect(Screen.width * 0.05f, Screen.height * 0.04f, Screen.width * 0.9f, Screen.height * 0.92f);
		GUI.Box(area, GUIContent.none, _window);
		float pad = 20f * _s;
		var inner = new Rect(area.x + pad, area.y + 16f * _s, area.width - pad * 2, area.height - 32f * _s);
		_tableWidth = inner.width - (SidebarWidth + 16f + 26f) * _s;
		GUILayout.BeginArea(inner);
		DrawTopBar();
		GUILayout.Space(10f * _s);
		GUILayout.BeginHorizontal();
		DrawSidebar();
		GUILayout.Space(16f * _s);
		GUILayout.BeginVertical();
		try
		{
			DrawConfirm();
			DrawMessage();
			DrawContent();
		}
		catch (Exception ex)
		{
			GUILayout.Label(L.T("显示出错: ", "Display error: ") + ex.Message, _warn);
		}
		GUILayout.EndVertical();
		GUILayout.EndHorizontal();
		GUILayout.EndArea();
	}

	private void DrawTopBar()
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label(TerminalMod.AppTitle, _title);
		GUILayout.FlexibleSpace();
		if (_data != null)
		{
			GUILayout.Label(L.F("第 {0} 天 · 每 {1} 秒自动刷新 · 「近 7 天」= 第 {2}–{0} 天", "Day {0} · refresh every {1} s · last 7 days = days {2}–{0}", _data.Day, AutoRefreshSeconds, _data.Day - 6), _mutedRight);
		}
		GUILayout.Space(12f * _s);
		if (GUILayout.Button(L.T("刷新", "Refresh"), _plainButton, W(90))) Defer(Refresh);
		if (GUILayout.Button(L.T("关闭 (F8)", "Close (F8)"), _plainButton, W(120))) Defer(() => SetVisible(false));
		GUILayout.EndHorizontal();
	}

	private void DrawSidebar()
	{
		GUILayout.BeginVertical(W(SidebarWidth));
		_sidebarScroll = GUILayout.BeginScrollView(_sidebarScroll);
		if (GUILayout.Button(L.T("全部汇总", "Overview"), _view == View.All ? _sideButtonSelected : _sideButton)) Defer(() => Select(View.All, 0));
		if (_data != null)
		{
			SidebarGroup(L.T("仓库", "Warehouses"), View.Warehouse, _data.Warehouses.Select(p => p.Name + "\n" + p.AddressText).ToList());
			SidebarGroup(L.T("工厂", "Factories"), View.Factory, _data.Factories.Select(p => p.Name + "\n" + p.AddressText).ToList());
			SidebarGroup(L.T("商店", "Stores"), View.Store, _data.Stores.Select(s => s.Name + "\n" + s.AddressText).ToList());
		}
		GUILayout.EndScrollView();
		GUILayout.EndVertical();
	}

	private void SidebarGroup(string label, View view, List<string> names)
	{
		GUILayout.Space(10f * _s);
		GUILayout.Label(label, _header);
		if (names.Count == 0)
		{
			GUILayout.Label(view == View.Store ? L.T("仓库配送线里还没有商店", "No warehouse route supplies a store") : L.T("还没有", "None yet"), _muted);
			return;
		}
		for (int i = 0; i < names.Count; i++)
		{
			bool selected = _view == view && _index == i;
			int index = i;
			if (GUILayout.Button(names[i], selected ? _sideButtonSelected : _sideButton)) Defer(() => Select(view, index));
		}
	}

	private void Select(View view, int index)
	{
		_view = view;
		_index = index;
		_tab = "stock";
		_contentScroll = Vector2.zero;
		CloseConfirm();
		_message = null;
	}

	private void DrawContent()
	{
		if (_data == null)
		{
			GUILayout.Label(L.T("暂无数据", "No data"), _muted);
			return;
		}
		switch (_view)
		{
			case View.Warehouse when _index < _data.Warehouses.Count:
				DrawWarehouse(_data.Warehouses[_index]);
				break;
			case View.Factory when _index < _data.Factories.Count:
				DrawFactory(_data.Factories[_index]);
				break;
			case View.Store when _index < _data.Stores.Count:
				DrawStore(_data.Stores[_index]);
				break;
			default:
				DrawAll();
				break;
		}
	}

	// ───────────── 全部汇总 ─────────────

	private void DrawAll()
	{
		GUILayout.Label(L.F("全部汇总 · {0} 个仓库 · {1} 个工厂 · {2} 家商店", "Overview · {0} warehouses · {1} factories · {2} stores", _data.Warehouses.Count, _data.Factories.Count, _data.Stores.Count), _h1);
		DrawTabs(("stock", L.T("库存汇总", "Stock")), ("plan", L.T("每周进货汇总", "Weekly supply")), ("restock", L.T("商铺补货目标", "Store restock targets")));
		if (_tab == "plan")
		{
			DrawPlan(_data.SupplyRows, L.T("所有仓库", "All warehouses"));
			return;
		}
		if (_tab == "restock")
		{
			DrawRestockAll();
			return;
		}
		Row(new Cell(L.T("货物", "Goods"), _header, 0), new Cell(L.T("仓库库存", "In warehouses"), _headerRight, 150), new Cell(L.T("工厂库存", "In factories"), _headerRight, 150), new Cell(L.T("合计", "Total"), _headerRight, 150));
		BeginScroll();
		if (_data.Totals.Count == 0) GUILayout.Label(L.T("还没有库存。", "No stock yet."), _muted);
		foreach (var t in _data.Totals)
		{
			Row(new Cell(t.name, _cell, 0), new Cell(N(t.warehouse), _cellRight, 150), new Cell(N(t.factory), _cellRight, 150), new Cell(N(t.warehouse + t.factory), _cellRightBold, 150));
			Line();
		}
		EndScroll();
	}

	private void DrawRestockAll()
	{
		var changes = StoreRestock.Plan(_data.Stores);
		GUILayout.BeginHorizontal();
		GUILayout.Label(changes.Count > 0 ? L.F("{0} 项补货目标和建议不一样", "{0} restock targets are different from the suggestion", changes.Count) : L.T("所有补货目标都和建议一样", "All restock targets match the suggestion"), changes.Count > 0 ? _warn : _good, GUILayout.ExpandWidth(false));
		GUILayout.Space(12f * _s);
		if (changes.Count > 0 && GUILayout.Button(L.T("全部按建议改", "Apply all suggestions"), _blueButton)) Defer(() => ConfirmRestock(changes));
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Label(L.T("建议补货目标 = 近 7 天单日最多卖 × 2（多备一天，哪天送货员没来也不断货）。近 7 天没卖出的商品不改。", "Suggested target = best day of the last 7 days × 2. The extra day prevents an empty shelf if one delivery does not come. The mod does not change items with no sales in 7 days."), _muted);
		Row(new Cell(L.T("商店", "Store"), _header, 230), new Cell(L.T("商品", "Item"), _header, 0), new Cell(L.T("每天平均卖", "Avg / day"), _headerRight, 110), new Cell(L.T("单日最多卖", "Best day"), _headerRight, 110),
			new Cell(L.T("现在的目标", "Current"), _headerRight, 120), new Cell(L.T("建议目标", "Suggested"), _headerRight, 120), new Cell(L.T("判断", "Verdict"), _headerRight, 230));
		BeginScroll();
		foreach (var store in _data.Stores)
		{
			foreach (var line in store.Lines)
			{
				Verdict(line, out string verdict, out GUIStyle style);
				Row(new Cell(store.Name, _cell, 230), new Cell(line.Name, _cell, 0), new Cell(N(line.Avg), _cellRight, 110), new Cell(N(line.Peak), _cellRight, 110),
					new Cell(N(line.Target), _cellRight, 120), new Cell(N(line.Suggest), _cellRightBold, 120), new Cell(verdict, style, 230));
				Line();
			}
		}
		EndScroll();
	}

	private void ConfirmRestock(List<StoreRestock.Change> changes)
	{
		var lines = changes.Select(c => c.Store.Name + " · " + c.Line.Name + L.T("：", ": ") + N(c.Line.Target) + " → " + N(c.Line.Suggest)).ToList();
		OpenConfirm(L.F("按建议改补货目标？（{0} 家店、{1} 项）", "Apply suggested restock targets? ({0} stores, {1} items)", changes.Select(c => c.Store).Distinct().Count(), changes.Count), lines, null,
			L.T("确认修改", "Confirm"), () => StoreRestock.Apply(changes));
	}

	// ───────────── 仓库 ─────────────

	private void DrawWarehouse(PlaceView place)
	{
		GUILayout.Label(L.T("[仓库] ", "[Warehouse] ") + place.Name + " · " + place.AddressText, _h1);
		GUILayout.Label(L.F("货架 {0} / {1} 箱", "Shelves {0} / {1} boxes", N(place.UsedBoxes), N(place.MaxBoxes)), _muted);
		DrawTabs(("stock", L.T("库存", "Stock")), ("in", L.T("进货来源", "Incoming")), ("out", L.T("送货去向", "Outgoing")), ("plan", L.T("每周进货", "Weekly supply")));

		if (_tab == "in")
		{
			BeginScroll();
			if (place.Sources.Count == 0) GUILayout.Label(L.T("这个仓库没有码头进口，也没有别处转入。", "Nothing is imported or transferred into this warehouse."), _muted);
			foreach (var card in place.Sources) DrawSourceCard(card);
			EndScroll();
			return;
		}
		if (_tab == "out")
		{
			DrawBalance(place);
			return;
		}
		if (_tab == "plan")
		{
			DrawPlan(_data.SupplyRows.Where(r => r.Warehouse == place.Registration).ToList(), place.Name);
			return;
		}

		Row(new Cell(L.T("货物", "Goods"), _header, 0), new Cell(L.T("现有库存", "In stock"), _headerRight, 140), new Cell(L.T("近7天运进", "In, 7 days"), _headerRight, 140), new Cell(L.T("近7天运出", "Out, 7 days"), _headerRight, 140), new Cell(L.T("还够用多久", "Lasts"), _headerRight, 180));
		BeginScroll();
		if (place.Stock.Count == 0) GUILayout.Label(L.T("这里没有库存。", "No stock here."), _muted);
		foreach (var line in place.Stock.OrderBy(DaysLeftKey))
		{
			DescribeDaysLeft(line, out string status, out GUIStyle style);
			Row(new Cell(line.Name, _cell, 0), new Cell(N(line.Stock), _cellRightBold, 140), new Cell(Nz(line.In7), _cellRight, 140), new Cell(Nz(line.Out7), _cellRight, 140), new Cell(status, style, 180));
			Line();
		}
		EndScroll();
		GUILayout.Label(L.F("运进包括码头进口和转入，运出包括送工厂和送商店。「还够用多久」= 现有库存 ÷ 每天运出，{0} 天以内标红。仓库没有机器，不消耗货物。", "In = imports and transfers. Out = deliveries to factories and stores. Lasts = stock ÷ out per day. Red shows {0} days or less. Warehouses have no machines. They do not use goods.", LowStockDays), _muted);
	}

	private static float DaysLeftKey(StockLine line)
	{
		if (line.Stock <= 0) return -1f;
		if (line.Out7 <= 0) return float.MaxValue;
		return line.Stock / (line.Out7 / 7f);
	}

	private void DescribeDaysLeft(StockLine line, out string status, out GUIStyle style)
	{
		float daily = line.Out7 / 7f;
		if (line.Stock <= 0)
		{
			status = L.T("已用完", "Empty");
			style = _warnRight;
		}
		else if (daily <= 0f)
		{
			status = L.T("没有运出", "No deliveries out");
			style = _mutedRight;
		}
		else
		{
			float days = line.Stock / daily;
			status = days < 1f ? L.T("不到 1 天", "< 1 day") : L.F("约 {0} 天", "~{0} days", Mathf.FloorToInt(days));
			style = days <= LowStockDays ? _warnRight : _goodRight;
		}
	}

	private void DrawSourceCard(SourceCard card)
	{
		GUILayout.BeginVertical(_card, W(760));
		GUILayout.BeginHorizontal();
		GUILayout.Label(card.Kind, card.IsTransfer ? _tagTransfer : _tagImport, GUILayout.ExpandWidth(false));
		GUILayout.Space(8f * _s);
		GUILayout.Label(card.Title, _h2, GUILayout.ExpandWidth(false));
		GUILayout.Space(8f * _s);
		GUILayout.Label(card.Schedule, _muted);
		GUILayout.EndHorizontal();
		float saved = _tableWidth;
		_tableWidth = (760f - 28f) * _s;
		Row(new Cell(L.T("货物", "Goods"), _header, 0), new Cell(card.Unit, _headerRight, 180));
		foreach (var item in card.Items)
		{
			Line();
			Row(new Cell(item.Key, _cell, 0), new Cell(N(item.Value), _cellRightBold, 180));
		}
		_tableWidth = saved;
		GUILayout.EndVertical();
		GUILayout.Space(10f * _s);
	}

	private void DrawBalance(PlaceView place)
	{
		float[] w = Widths(280, 120, 320, 0, 150);
		HeaderRow(w, (L.T("货物", "Goods"), _header), (L.T("每周进货", "In / week"), _headerRight), (L.T("① 先送工厂", "① Factories first"), _header), (L.T("② 送自家商铺", "② Your stores"), _header), (L.T("每周剩余 / 缺", "Weekly ±"), _headerRight));
		BeginScroll();
		if (place.Balance.Count == 0) GUILayout.Label(L.T("这个仓库还没有配送线。", "This warehouse has no delivery routes yet."), _muted);
		foreach (var line in place.Balance)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(line.Name, _cellWrap, GUILayout.Width(w[0]));
			GUILayout.Label(Nz(line.InWeekly), _cellRightBold, GUILayout.Width(w[1]));
			GUILayout.BeginVertical(GUILayout.Width(w[2]));
			GUILayout.Label(Describe(line.Factories), _cellWrap, GUILayout.Width(w[2]));
			if (line.Factories.Count > 0 && line.Stores.Count > 0) GUILayout.Label(L.T("送完工厂还剩 ", "Left after factories: ") + N(line.AfterFactories), _muted, GUILayout.Width(w[2]));
			GUILayout.EndVertical();
			GUILayout.Label(Describe(line.Stores), _cellWrap, GUILayout.Width(w[3]));
			GUILayout.Label(Signed(line.Remain), line.Remain < 0 ? _warnRight : _goodRight, GUILayout.Width(w[4]));
			GUILayout.EndHorizontal();
			Line();
		}
		EndScroll();
		GUILayout.Label(L.T("每周剩余 / 缺 = 每周进货 − 工厂每周需要（按机器用料）− 商铺近 7 天销量。先保证工厂开工，剩下的再给商铺。每周进货：码头进口按合同数量，别处转入按近 7 天转入量。",
			"Weekly ± = supply − factory need − store sales. The factory need comes from the machine recipes. The store sales are for the last 7 days. Factories get goods first. Stores get the remainder. Import supply is the contract amount. Transfer supply is the amount of the last 7 days."), _muted);
	}

	private static string Describe(List<KeyValuePair<string, int>> list)
	{
		return list.Count == 0 ? "—" : string.Join(L.Sep, list.Select(x => x.Key + " " + N(x.Value)));
	}

	// ───────────── 工厂 ─────────────

	private void DrawFactory(PlaceView place)
	{
		GUILayout.Label(L.T("[工厂] ", "[Factory] ") + place.Name + " · " + place.AddressText, _h1);
		GUILayout.Label(L.F("货架 {0} / {1} 箱", "Shelves {0} / {1} boxes", N(place.UsedBoxes), N(place.MaxBoxes)), _muted);
		BeginScroll();

		int? produced = place.HasLedger ? place.Yesterday.Sum(y => y.Produced ?? 0) : (int?)null;
		GUILayout.BeginHorizontal();
		Tile(L.T("昨天生产", "Made yesterday"), produced.HasValue ? N(produced.Value) : L.T("等记账", "No data yet"));
		Tile(L.T("给自家供货", "To your buildings"), N(place.Yesterday.Sum(y => y.Own)));
		Tile(L.T("出口", "Exported"), N(place.Yesterday.Sum(y => y.Exported)));
		Tile(L.T("出口收入", "Export revenue"), Money(place.Yesterday.Sum(y => y.Revenue)));
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Space(14f * _s);

		DrawMachines(place);
		GUILayout.Space(14f * _s);

		GUILayout.Label(L.F("昨天（第 {0} 天）生产的货去了哪里", "Where yesterday's output went (day {0})", _data.Day - 1), _h2);
		Row(new Cell(L.T("产品", "Product"), _header, 0), new Cell(L.T("昨天生产", "Made"), _headerRight, 130), new Cell(L.T("给自家供货", "To your buildings"), _headerRight, 140), new Cell(L.T("出口", "Exported"), _headerRight, 120),
			new Cell(L.T("出口收入", "Revenue"), _headerRight, 150), new Cell(L.T("留在厂里", "Kept"), _headerRight, 130));
		foreach (var y in place.Yesterday)
		{
			string left = y.Produced.HasValue ? N(Math.Max(0, y.Produced.Value - y.Own - y.Exported)) : "—";
			Row(new Cell(y.Name, _cell, 0), new Cell(y.Produced.HasValue ? N(y.Produced.Value) : L.T("等记账", "No data yet"), _cellRightBold, 130), new Cell(N(y.Own), _cellRight, 140),
				new Cell(Nz(y.Exported), _cellRight, 120), new Cell(Money(y.Revenue), _goodRight, 150), new Cell(left, _cellRight, 130));
			Line();
		}
		GUILayout.Label(place.HasLedger
			? L.T("出口收入 = 出口数量 × 当天出口单价。生产量由 mod 每天 0 点给库存拍快照算出来。", "Revenue = exported amount × export price of that day. The mod calculates the output from a stock record at midnight.")
			: L.T("「昨天生产」游戏不记录，由 mod 每天 0 点给库存拍快照算出来：装好 mod 后过两次午夜才有数。出口和给自家供货来自游戏记录，现在就准。", "The game does not record factory output. The mod records the stock every day at midnight. \"Made\" shows after two midnights. Exports and deliveries come from game records. These values are correct now."), _muted);
		GUILayout.Space(14f * _s);

		var mine = place.WeeklyNeed
			.Select(n => (need: n, row: _data.SupplyRows.FirstOrDefault(r => r.Item == n.Key && r.Dests.Any(d => d.Key == place.Name))))
			.Where(x => x.row != null).ToList();
		GUILayout.BeginHorizontal();
		GUILayout.Label(L.T("原料从哪个仓库来", "Where the raw materials come from"), _h2, GUILayout.ExpandWidth(false));
		GUILayout.Space(12f * _s);
		DrawOrderSummary(mine.Select(x => x.row));
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		float[] w = Widths(200, 140, 220, 0);
		HeaderRow(w, (L.T("原料", "Material"), _header), (L.T("本厂每周需要", "Need / week"), _headerRight), (L.T("供货仓库", "From warehouse"), _header), (L.T("仓库的进货", "Warehouse supply"), _header));
		if (mine.Count == 0) GUILayout.Label(L.T("机器没有用原料。", "The machines use no materials."), _muted);
		foreach (var (need, row) in mine)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(row.Name, _cellWrap, GUILayout.Width(w[0]));
			GUILayout.Label(N(need.Value), _cellRightBold, GUILayout.Width(w[1]));
			GUILayout.Label(row.WarehouseName, row.Warehouse == null ? _warn : _cellWrap, GUILayout.Width(w[2]));
			DrawSupply(row, w[3], row.Need != need.Value ? L.T(" · 仓库每周共需要 ", " · warehouse needs ") + N(row.Need) + L.T("", " / week") : "");
			GUILayout.EndHorizontal();
			Line();
		}
		GUILayout.Label(L.T("原料先进仓库，物流经理每天凌晨 2 点从仓库送到工厂。在这里点下单，就是给供货仓库下码头进口单，数量按仓库每周共需要。", "Raw materials go to a warehouse first. The logistics manager moves them to the factory at 2:00 every day. Order puts an import order on the supplying warehouse. The amount is the total weekly need of that warehouse."), _muted);
		EndScroll();
	}

	// 每组机器一行标题，下面把原料排成几列：原料 / 每天共用料 / 每周共用料
	private void DrawMachines(PlaceView place)
	{
		GUILayout.Label(L.T("机器", "Machines"), _h2);
		if (place.Machines.Count == 0)
		{
			GUILayout.Label(L.T("没有选了配方的机器。", "No machine has a recipe."), _muted);
			return;
		}
		const float nameW = 170f, dayW = 120f, weekW = 130f, gap = 36f;
		float groupW = (nameW + dayW + weekW + gap) * _s;
		int perLine = Mathf.Max(1, Mathf.FloorToInt((_tableWidth - 24f * _s) / groupW));
		foreach (var m in place.Machines)
		{
			string schedule = m.WeeklyHours == 0
				? L.F("{0} 台 · 没有排班", "{0} × · no shifts", m.Count)
				: L.F("{0} 台 × 每天{1}{2} 小时 · 每周 {3} 天", "{0} × {1}{2} h/day · {3} days/week", m.Count, m.SameHoursEveryDay ? (L.Chinese ? " " : "") : (L.Chinese ? "约 " : "~"), Hours(m.HoursPerDay), m.WorkDays);
			string output = m.WorkDays == 0 ? "0" : N(m.WeeklyOutput / m.WorkDays);
			GUILayout.BeginVertical(_card);
			GUILayout.BeginHorizontal();
			GUILayout.Label(m.Name, _cellBold, GUILayout.ExpandWidth(false));
			GUILayout.Space(16f * _s);
			GUILayout.Label(schedule, _cell, GUILayout.ExpandWidth(false));
			GUILayout.FlexibleSpace();
			GUILayout.Label(L.T("每天共生产 ", "Makes per day: ") + GameData.Localize(m.OutputItem) + " " + output, _cellRightBold, GUILayout.ExpandWidth(false));
			GUILayout.EndHorizontal();
			for (int start = 0; start < m.Inputs.Count; start += perLine)
			{
				GUILayout.BeginHorizontal();
				for (int i = start; i < Math.Min(start + perLine, m.Inputs.Count); i++)
				{
					var input = m.Inputs[i];
					GUILayout.Label(GameData.Localize(input.item), _muted, W(nameW));
					GUILayout.Label(L.T("每天 ", "day ") + N(input.amount * m.Count * m.HoursPerDay), _cellRight, W(dayW));
					GUILayout.Label(L.T("每周 ", "week ") + N(input.amount * m.Count * m.WeeklyHours), _cellRightBold, W(weekW));
					GUILayout.Space(gap * _s);
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
		}
	}

	private void Tile(string label, string value)
	{
		GUILayout.BeginVertical(_card, W(220));
		GUILayout.Label(label, _tileLabel);
		GUILayout.Label(value, _tileValue);
		GUILayout.EndVertical();
		GUILayout.Space(10f * _s);
	}

	// ───────────── 每周进货（按仓库） ─────────────

	private void DrawPlan(List<SupplyRow> rows, string scope)
	{
		GUILayout.BeginHorizontal();
		DrawOrderSummary(rows);
		GUILayout.Space(16f * _s);
		if (rows.Count > 0 && GUILayout.Button(L.T("一键按需要调整每周进货", "Match weekly imports to need"), _blueButton)) Defer(() => ConfirmImports(rows, false, scope + L.T("：按需要调整每周进货？", ": change the weekly imports to the weekly need?")));
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Label(L.T("每周需要 = 送工厂的原料（按机器用料）+ 送自家商铺的货（按近 7 天销量）。原料先进仓库，再由物流经理每天凌晨 2 点送到工厂。", "Weekly need = raw materials for factories + goods for your stores. The raw materials come from the machine recipes. The store goods come from the last 7 days of sales. Raw materials go to a warehouse first. The logistics manager moves them to the factories at 2:00 every day."), _muted);
		float[] w = Widths(240, 0, 110, 110, 110, 420);
		HeaderRow(w, (L.T("货物", "Goods"), _header), (L.T("送到哪里（每周）", "Goes to (per week)"), _header), (L.T("每周需要", "Need"), _headerRight), (L.T("每周进货", "Supply"), _headerRight), (L.T("剩余 / 缺", "±"), _headerRight), (L.T("进货", "Order"), _header));
		BeginScroll();
		if (rows.Count == 0) GUILayout.Label(L.T("这里没有需要进货的货物。", "No item here needs supply."), _muted);
		foreach (var group in rows.GroupBy(r => r.WarehouseName))
		{
			var first = group.First();
			GUILayout.Space(8f * _s);
			GUILayout.BeginHorizontal();
			GUILayout.Label(group.Key, first.Warehouse == null ? _warn : _h2, GUILayout.ExpandWidth(false));
			GUILayout.Space(10f * _s);
			if (first.Warehouse != null) GUILayout.Label(GameData.AddressText(first.Warehouse), _muted);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			Line();
			foreach (var row in group)
			{
				bool known = row.Warehouse != null;
				var dests = row.Dests.Select(d => d.Key + " " + N(d.Value)).Concat(row.Stores.Select(d => d.Key + " " + N(d.Value)));
				GUILayout.BeginHorizontal();
				GUILayout.Label(row.Name, row.IsRaw ? _cellBold : _cellWrap, GUILayout.Width(w[0]));
				GUILayout.Label(row.Need == 0 ? "—" : string.Join(L.Sep, dests), _cellWrap, GUILayout.Width(w[1]));
				GUILayout.Label(Nz(row.Need), _cellRightBold, GUILayout.Width(w[2]));
				GUILayout.Label(row.Kind == SupplyKind.None ? "—" : N(row.Supplied), _cellRight, GUILayout.Width(w[3]));
				GUILayout.Label(known ? Signed(row.Diff) : "—", row.Diff < 0 ? _warnRight : _goodRight, GUILayout.Width(w[4]));
				DrawSupply(row, w[5], "");
				GUILayout.EndHorizontal();
				Line();
			}
		}
		EndScroll();
		GUILayout.Label(L.T("粗体是工厂原料。剩余 / 缺 = 每周进货 − 每周需要。一键调整会把码头进口的每周数量改成每周需要（多的减、少的补，取整到 10），批发合同和自家工厂供货的不动。", "Bold items are factory raw materials. ± = weekly supply − weekly need. The one-click button sets each weekly import amount to the weekly need. It increases or decreases the amount and rounds it up to 10. It does not change wholesale contracts or the output of your factories."), _muted);
	}

	private void DrawOrderSummary(IEnumerable<SupplyRow> rows)
	{
		var list = rows.ToList();
		int missing = list.Count(r => r.Kind == SupplyKind.None && r.Need > 0);
		int short_ = list.Count(r => (r.Kind == SupplyKind.Import || r.Kind == SupplyKind.Wholesale) && r.Diff < 0);
		string text = missing > 0 ? L.F("还有 {0} 种没下单", "{0} not ordered", missing) : L.T("全部已下单", "All ordered");
		if (short_ > 0) text += L.F(" · {0} 种每周不够", " · {0} with a weekly shortage", short_);
		GUILayout.Label(text, missing + short_ > 0 ? _warn : _good, GUILayout.ExpandWidth(false));
	}

	// 进货状态：已下单 / 下单 / 自家工厂供货 / 没有仓库送，后面跟一行说明
	private void DrawSupply(SupplyRow row, float width, string extraNote)
	{
		GUILayout.BeginHorizontal(GUILayout.Width(width));
		switch (row.Kind)
		{
			case SupplyKind.Import:
				GUILayout.Label(L.T("已下单", "Ordered"), _badgeGood, GUILayout.ExpandWidth(false));
				if (row.Diff < 0 && GUILayout.Button(L.T("加量", "Increase"), _redButton, GUILayout.ExpandWidth(false))) Defer(() => ConfirmImports(new[] { row }, true, row.Name + L.T("：加量？", ": increase the amount?")));
				break;
			case SupplyKind.Wholesale:
				GUILayout.Label(L.T("批发进货", "Wholesale"), _badgeGood, GUILayout.ExpandWidth(false));
				break;
			case SupplyKind.Internal:
				GUILayout.Label(L.T("自家工厂供货", "Own factory"), _badgeGray, GUILayout.ExpandWidth(false));
				break;
			default:
				if (row.Warehouse == null) GUILayout.Label(L.T("没有仓库送", "No warehouse"), _badgeWarn, GUILayout.ExpandWidth(false));
				else if (row.Need > 0 && GUILayout.Button(L.T("下单", "Order"), _redButton, GUILayout.ExpandWidth(false))) Defer(() => ConfirmImports(new[] { row }, true, row.Name + L.T("：下单？", ": order?")));
				break;
		}
		GUILayout.Space(10f * _s);
		GUILayout.Label(row.SupplyNote + extraNote, _muted);
		GUILayout.EndHorizontal();
	}

	private void ConfirmImports(IEnumerable<SupplyRow> rows, bool onlyIncrease, string title)
	{
		var plan = ImportOrdering.Plan(rows, onlyIncrease);
		var lines = plan.Changes.Select(c => c.Row.WarehouseName + " · " + c.Row.Name + L.T("：每周 ", ": per week ") + N(c.OldAmount) + " → " + N(c.NewAmount)
			+ L.T("（", " (") + c.ImporterName + (c.OldAmount == 0 ? L.T("，新加", ", new item") : "") + L.T("）", ")")).ToList();
		lines.AddRange(plan.Activations);
		if (plan.Changes.Count == 0 && plan.Problems.Count == 0) lines.Add(L.T("每周进货已经和需要一样，不用改。", "The weekly supply is equal to the need. No change is necessary."));
		OpenConfirm(title, lines, plan.Problems, plan.Changes.Count > 0 ? L.T("确认下单", "Confirm order") : null, () => ImportOrdering.Apply(plan));
	}

	// ───────────── 商店 ─────────────

	private void DrawStore(StoreView store)
	{
		GUILayout.Label(L.T("[商店] ", "[Store] ") + store.Name + " · " + store.AddressText, _h1);
		var changes = StoreRestock.Plan(new[] { store });
		GUILayout.BeginHorizontal();
		GUILayout.Label(L.F("由 {0} 供货 · 销量为近 7 天每天实际卖出", "Supplier: {0} · sales are the real daily sales of the last 7 days", store.Supplier), _muted, GUILayout.ExpandWidth(false));
		GUILayout.Space(16f * _s);
		if (changes.Count > 0 && GUILayout.Button(L.T("这家店按建议改", "Apply suggestions for this store"), _blueButton)) Defer(() => ConfirmRestock(changes));
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		Row(new Cell(L.T("商品", "Item"), _header, 0), new Cell(L.T("店内库存", "In store"), _headerRight, 110), new Cell(L.T("每天平均卖", "Avg / day"), _headerRight, 110), new Cell(L.T("单日最多卖", "Best day"), _headerRight, 110),
			new Cell(L.T("现在的补货目标", "Current target"), _headerRight, 150), new Cell(L.T("建议补货目标", "Suggested"), _headerRight, 140), new Cell(L.T("今晚会送", "Tonight"), _headerRight, 100), new Cell(L.T("判断", "Verdict"), _headerRight, 230));
		BeginScroll();
		if (store.Lines.Count == 0) GUILayout.Label(L.T("配送线里没有给这家店设补货目标。", "No delivery route has restock targets for this store."), _muted);
		foreach (var line in store.Lines)
		{
			Verdict(line, out string verdict, out GUIStyle style);
			Row(new Cell(line.Name, _cell, 0), new Cell(N(line.Stock), _cellRight, 110), new Cell(N(line.Avg), _cellRight, 110), new Cell(N(line.Peak), _cellRight, 110),
				new Cell(N(line.Target), _cellRight, 150), new Cell(N(line.Suggest), _cellRightBold, 140), new Cell(N(line.Tonight), _cellRight, 100), new Cell(verdict, style, 230));
			Line();
		}
		EndScroll();
		GUILayout.Label(L.T("物流经理每天凌晨 2 点把店里补到「补货目标」，所以今晚会送 = 补货目标 − 店内库存。建议补货目标 = 近 7 天单日最多卖的量 × 2，多备一天：哪天送货员没来、没补上货，店里也够卖到下一次补货。", "The logistics manager fills the store to its target at 2:00 every day. Tonight = target − in store. Suggested target = best day of the last 7 days × 2. The extra day prevents an empty shelf if one delivery does not come."), _muted);
	}

	private void Verdict(StoreLine line, out string verdict, out GUIStyle style)
	{
		if (line.Peak == 0)
		{
			verdict = L.T("近 7 天没卖出", "No sales in 7 days");
			style = _mutedRight;
		}
		else if (line.Target < line.Peak)
		{
			verdict = L.T("目标太低，每天都会断货", "Too low. Sells out every day.");
			style = _warnRight;
		}
		else if (line.Target < line.Suggest)
		{
			verdict = L.T("漏送一天就会断货", "Empty after one missed delivery");
			style = _orangeRight;
		}
		else if (line.Target > line.Suggest * 1.5f)
		{
			verdict = L.T("目标偏高，压货", "Too high. Too much stock.");
			style = _orangeRight;
		}
		else
		{
			verdict = L.T("合适", "OK");
			style = _goodRight;
		}
	}

	// ───────────── 确认框 / 提示 ─────────────

	private void OpenConfirm(string title, List<string> lines, List<string> problems, string button, Func<string> action)
	{
		_message = null;
		_confirmTitle = title;
		_confirmLines = lines;
		_confirmProblems = problems ?? new List<string>();
		_confirmButton = button;
		_confirmAction = action;
		_confirmScroll = Vector2.zero;
	}

	private void CloseConfirm()
	{
		_confirmTitle = null;
		_confirmLines = null;
		_confirmProblems = null;
		_confirmButton = null;
		_confirmAction = null;
	}

	private void DrawConfirm()
	{
		if (_confirmTitle == null) return;
		GUILayout.BeginVertical(_card);
		GUILayout.Label(_confirmTitle, _h2);
		int count = _confirmLines.Count + _confirmProblems.Count;
		_confirmScroll = GUILayout.BeginScrollView(_confirmScroll, GUILayout.Height((Mathf.Min(count, 12) * 30f + 10f) * _s));
		foreach (string line in _confirmLines) GUILayout.Label("· " + line, _cellWrap);
		foreach (string problem in _confirmProblems) GUILayout.Label("· " + problem, _warn);
		GUILayout.EndScrollView();
		GUILayout.BeginHorizontal();
		if (_confirmButton != null && GUILayout.Button(_confirmButton, _blueButton, GUILayout.ExpandWidth(false))) Defer(RunConfirm);
		GUILayout.Space(8f * _s);
		if (GUILayout.Button(_confirmButton != null ? L.T("取消", "Cancel") : L.T("知道了", "OK"), _plainButton, W(120))) Defer(CloseConfirm);
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.EndVertical();
		GUILayout.Space(8f * _s);
	}

	private void RunConfirm()
	{
		var action = _confirmAction;
		CloseConfirm();
		if (action == null) return;
		try
		{
			_message = action();
			_messageGood = true;
		}
		catch (Exception ex)
		{
			_message = L.T("操作失败：", "Action failed: ") + ex.Message;
			_messageGood = false;
			TerminalMod.LogError("Action failed: " + ex);
		}
		Refresh();
	}

	private void DrawMessage()
	{
		if (string.IsNullOrEmpty(_message)) return;
		GUILayout.BeginHorizontal();
		GUILayout.Label(_message, _messageGood ? _good : _warn, GUILayout.ExpandWidth(false));
		GUILayout.Space(8f * _s);
		if (GUILayout.Button("×", _plainButton, W(40))) Defer(() => _message = null);
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
	}

	// ───────────── 通用 ─────────────

	private void DrawTabs(params (string id, string label)[] tabs)
	{
		if (!tabs.Any(t => t.id == _tab)) _tab = tabs[0].id;
		GUILayout.BeginHorizontal();
		foreach (var (id, label) in tabs)
		{
			if (GUILayout.Button(label, _tab == id ? _tabButtonSelected : _tabButton, GUILayout.ExpandWidth(false)) && _tab != id)
			{
				Defer(() =>
				{
					_tab = id;
					_contentScroll = Vector2.zero;
					CloseConfirm();
					_message = null;
				});
			}
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		Line();
		GUILayout.Space(4f * _s);
	}

	// 设计宽度（1080p 像素）→ 屏幕像素；0 的列平分剩余宽度
	private float[] Widths(params float[] design)
	{
		float fixedSum = design.Where(x => x > 0).Sum() * _s;
		int flex = design.Count(x => x <= 0);
		float flexWidth = flex == 0 ? 0f : Mathf.Max(120f * _s, (_tableWidth - fixedSum) / flex);
		return design.Select(x => x > 0 ? Mathf.Round(x * _s) : Mathf.Floor(flexWidth)).ToArray();
	}

	private void HeaderRow(float[] widths, params (string text, GUIStyle style)[] cells)
	{
		GUILayout.BeginHorizontal();
		for (int i = 0; i < cells.Length; i++) GUILayout.Label(cells[i].text, cells[i].style, GUILayout.Width(widths[i]));
		GUILayout.EndHorizontal();
	}

	private void Row(params Cell[] cells)
	{
		float[] widths = Widths(cells.Select(c => c.Width).ToArray());
		GUILayout.BeginHorizontal();
		for (int i = 0; i < cells.Length; i++) GUILayout.Label(cells[i].Text, cells[i].Style, GUILayout.Width(widths[i]));
		GUILayout.EndHorizontal();
	}

	private GUILayoutOption W(float designWidth) => GUILayout.Width(Mathf.Round(designWidth * _s));

	private void Line()
	{
		GUILayout.Box(GUIContent.none, _line, GUILayout.Height(1), GUILayout.ExpandWidth(true));
	}

	private void BeginScroll()
	{
		_contentScroll = GUILayout.BeginScrollView(_contentScroll);
	}

	private void EndScroll()
	{
		GUILayout.EndScrollView();
	}

	private static string N(float value) => GameData.N(value);

	private static string Nz(int value) => value == 0 ? "—" : N(value);

	private static string Signed(int value) => (value > 0 ? "+" : "") + N(value);

	private static string Money(float value) => "$" + N(value);

	private static string Hours(float value) => Mathf.Approximately(value, Mathf.Round(value)) ? Mathf.RoundToInt(value).ToString() : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

	// ───────────── 样式（随屏幕缩放重建） ─────────────

	private void EnsureStyles()
	{
		if (Mathf.Approximately(_stylesScale, _s)) return;
		_stylesScale = _s;

		if (_font == null)
		{
			foreach (string name in FontCandidates)
			{
				try
				{
					_font = Font.CreateDynamicFontFromOSFont(name, 18);
					if (_font != null) break;
				}
				catch
				{
				}
			}
		}

		_window = new GUIStyle(GUI.skin.box) { normal = { background = Solid(new Color(0.96f, 0.97f, 0.98f, 0.98f)) } };
		_title = Label(28, TextColor, bold: true);
		_h1 = Label(21, TextColor, bold: true);
		_h2 = Label(18, TextColor, bold: true);
		_header = Label(16, TextColor, bold: true);
		_headerRight = Right(_header);
		_cell = Label(17, TextColor);
		_cellWrap = Wrap(_cell);
		_cellBold = Wrap(Label(17, TextColor, bold: true));
		_cellRight = Right(_cell);
		_cellRightBold = Right(Label(17, TextColor, bold: true));
		_good = Label(17, GoodColor, bold: true);
		_goodRight = Right(_good);
		_warn = Wrap(Label(17, WarnColor, bold: true));
		_warnRight = Right(Label(17, WarnColor, bold: true));
		_orangeRight = Right(Label(17, OrangeColor, bold: true));
		_mutedRight = Right(Label(15, MutedColor));
		_muted = Wrap(Label(15, MutedColor));
		_tileLabel = Label(15, MutedColor);
		_tileValue = Label(28, TextColor, bold: true);
		_line = new GUIStyle { normal = { background = Solid(new Color(0.85f, 0.87f, 0.9f)) }, margin = Offset(0, 0, 2, 2) };
		_card = new GUIStyle(GUI.skin.box)
		{
			normal = { background = Solid(Color.white) },
			padding = Offset(14, 14, 10, 10),
			margin = Offset(0, 0, 4, 4),
		};
		_tagImport = Badge(BlueColor, Color.white);
		_tagTransfer = Badge(new Color(0.06f, 0.43f, 0.43f), Color.white);

		_plainButton = Button(16, Color.white, TextColor, 12, 8);
		_sideButton = Button(17, LightColor, TextColor, 12, 8);
		_sideButton.alignment = TextAnchor.MiddleLeft;
		_sideButtonSelected = Button(17, BlueColor, Color.white, 12, 8);
		_sideButtonSelected.alignment = TextAnchor.MiddleLeft;
		_tabButton = Button(17, LightColor, TextColor, 18, 8);
		_tabButtonSelected = Button(17, BlueColor, Color.white, 18, 8);
		_redButton = Button(16, WarnColor, Color.white, 18, 6);
		_redButton.fontStyle = FontStyle.Bold;
		_blueButton = Button(16, BlueColor, Color.white, 20, 8);
		_blueButton.fontStyle = FontStyle.Bold;
		_badgeGood = Badge(GoodColor, Color.white);
		_badgeGray = Badge(LightColor, new Color(0.24f, 0.27f, 0.31f));
		_badgeWarn = Badge(new Color(0.99f, 0.9f, 0.9f), WarnColor);
	}

	private int Size(int designSize) => Mathf.Max(8, Mathf.RoundToInt(designSize * _s));

	private RectOffset Offset(int left, int right, int top, int bottom)
	{
		return new RectOffset(Mathf.RoundToInt(left * _s), Mathf.RoundToInt(right * _s), Mathf.RoundToInt(top * _s), Mathf.RoundToInt(bottom * _s));
	}

	private GUIStyle Label(int size, Color color, bool bold = false)
	{
		return new GUIStyle(GUI.skin.label)
		{
			fontSize = Size(size),
			fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
			alignment = TextAnchor.MiddleLeft,
			wordWrap = false,
			clipping = TextClipping.Clip,
			padding = Offset(4, 4, 3, 3),
			margin = Offset(0, 0, 0, 0),
			normal = { textColor = color },
		};
	}

	private static GUIStyle Right(GUIStyle style)
	{
		return new GUIStyle(style) { alignment = TextAnchor.MiddleRight };
	}

	private static GUIStyle Wrap(GUIStyle style)
	{
		return new GUIStyle(style) { wordWrap = true };
	}

	private GUIStyle Badge(Color background, Color text)
	{
		return new GUIStyle(GUI.skin.label)
		{
			fontSize = Size(15),
			fontStyle = FontStyle.Bold,
			alignment = TextAnchor.MiddleCenter,
			padding = Offset(14, 14, 6, 6),
			margin = Offset(0, 4, 2, 2),
			normal = { background = Solid(background), textColor = text },
		};
	}

	private GUIStyle Button(int size, Color background, Color text, int padX, int padY)
	{
		var hover = Color.Lerp(background, Color.black, 0.08f);
		return new GUIStyle(GUI.skin.button)
		{
			fontSize = Size(size),
			padding = Offset(padX, padX, padY, padY),
			margin = Offset(0, 4, 2, 2),
			normal = { background = Solid(background), textColor = text },
			hover = { background = Solid(hover), textColor = text },
			active = { background = Solid(Color.Lerp(background, Color.black, 0.16f)), textColor = text },
		};
	}

	private static Texture2D Solid(Color color)
	{
		var texture = new Texture2D(1, 1);
		texture.SetPixel(0, 0, color);
		texture.Apply();
		return texture;
	}
}
