using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Factories.Recipes;
using BigAmbitions.Items;
using BigAmbitions.Tags;
using Buildings.Office.Headquarters;
using Entities;
using Helpers;
using Localizor;
using Streets;

namespace LogisticsTerminal;

public sealed class StockLine
{
	public string Item;
	public string Name;
	public int Stock;
	public int In7;
	public int Out7;
}

public sealed class SourceCard
{
	public string Kind;
	public bool IsTransfer;
	public string Title;
	public string Schedule;
	public string Unit;
	public List<KeyValuePair<string, int>> Items = new List<KeyValuePair<string, int>>();
}

public sealed class BalanceLine
{
	public string Item;
	public string Name;
	public int InWeekly;
	public List<KeyValuePair<string, int>> Factories = new List<KeyValuePair<string, int>>();
	public List<KeyValuePair<string, int>> Stores = new List<KeyValuePair<string, int>>();
	public int AfterFactories => InWeekly - Factories.Sum(x => x.Value);
	public int Remain => AfterFactories - Stores.Sum(x => x.Value);
}

public sealed class MachineGroup
{
	public string Name;
	public int Count;
	public int WorkDays;
	// 每台每周开工小时（同组的机器排班一样）
	public int WeeklyHours;
	public bool SameHoursEveryDay;
	public List<RecipeItem> Inputs;
	public string OutputItem;
	// 全组每周产量（按排班里每个员工的技能算）
	public float WeeklyOutput;

	public float HoursPerDay => WorkDays == 0 ? 0f : (float)WeeklyHours / WorkDays;
}

public sealed class YesterdayLine
{
	public string Name;
	// 没有记账数据时为 null
	public int? Produced;
	public int Own;
	public int Exported;
	public float Revenue;
}

public sealed class PlaceView
{
	public Entities.Warehouse Registration;
	public string Name;
	public string AddressText;
	public bool IsFactory;
	public int UsedBoxes;
	public int MaxBoxes;
	public List<StockLine> Stock = new List<StockLine>();

	// 仓库
	public List<SourceCard> Sources = new List<SourceCard>();
	public List<BalanceLine> Balance = new List<BalanceLine>();

	// 工厂
	public List<MachineGroup> Machines = new List<MachineGroup>();
	public List<YesterdayLine> Yesterday = new List<YesterdayLine>();
	public bool HasLedger;
	// 原料 → 每周需要（按顺序）
	public List<KeyValuePair<string, int>> WeeklyNeed = new List<KeyValuePair<string, int>>();
}

public sealed class StoreLine
{
	public string Item;
	public string Name;
	// 这家店这种货在各条配送线里的补货目标（一般只有一个）
	public List<ItemAmountTarget> Targets = new List<ItemAmountTarget>();
	public int Stock;
	public float Avg;
	public int Peak;
	public int Target;
	public int Suggest => (int)Math.Ceiling(Peak * 2 / 10f) * 10;
	public int Tonight => Math.Max(0, Target - Stock);
}

public sealed class StoreView
{
	public BuildingRegistration Registration;
	public string Name;
	public string AddressText;
	public string Supplier;
	public List<StoreLine> Lines = new List<StoreLine>();
}

public enum SupplyKind
{
	None,
	Import,
	// 只有批发商送货合同（一键调整不改批发合同）
	Wholesale,
	Internal,
}

// 一个仓库每周要进的一种货：先送工厂（原料），再送自家商铺。
// 仓库为空表示没有仓库给工厂送这种原料。
public sealed class SupplyRow
{
	public string Key;
	public string Item;
	public string Name;
	public Entities.Warehouse Warehouse;
	public string WarehouseName;
	// 工厂 → 每周需要
	public List<KeyValuePair<string, int>> Dests = new List<KeyValuePair<string, int>>();
	// 商铺 → 近 7 天销量
	public List<KeyValuePair<string, int>> Stores = new List<KeyValuePair<string, int>>();
	public bool IsRaw => Dests.Count > 0;
	public int Need;
	public SupplyKind Kind;
	public int Supplied;
	public string SupplyNote;

	public int Diff => Supplied - Need;
}

public sealed class TerminalData
{
	public int Day;
	public List<PlaceView> Warehouses = new List<PlaceView>();
	public List<PlaceView> Factories = new List<PlaceView>();
	public List<StoreView> Stores = new List<StoreView>();
	public List<SupplyRow> SupplyRows = new List<SupplyRow>();
	// 全部汇总：货物 → (仓库库存, 工厂库存)
	public List<(string name, int warehouse, int factory)> Totals = new List<(string, int, int)>();

	public static TerminalData Capture()
	{
		var data = new TerminalData();
		var save = SaveGameManager.Current;
		if (save == null) return data;
		data.Day = save.Day;
		new Builder(data, save).Build();
		return data;
	}

	private sealed class Builder
	{
		private readonly TerminalData _data;
		private readonly GameInstance _save;
		private readonly List<LogisticsManagerPlan> _plans;
		private readonly HashSet<string> _producedByOwnFactories = new HashSet<string>();

		public Builder(TerminalData data, GameInstance save)
		{
			_data = data;
			_save = save;
			_plans = save.logisticsManagerPlans ?? new List<LogisticsManagerPlan>();
		}

		public void Build()
		{
			foreach (var warehouse in GameData.PlayerWarehouses()) _data.Warehouses.Add(Safe(() => BasePlace(warehouse, false)));
			foreach (var factory in GameData.PlayerFactories()) _data.Factories.Add(Safe(() => BasePlace(factory, true)));
			_data.Warehouses.RemoveAll(x => x == null);
			_data.Factories.RemoveAll(x => x == null);

			foreach (var factory in _data.Factories) Try(() => BuildFactory(factory));
			foreach (var store in StoreRegistrations()) Try(() => _data.Stores.Add(BuildStore(store)));
			foreach (var warehouse in _data.Warehouses) Try(() => BuildWarehouse(warehouse));
			Try(BuildSupplyRows);
			Try(BuildTotals);
		}

		private PlaceView BasePlace(Entities.Warehouse registration, bool isFactory)
		{
			var place = new PlaceView
			{
				Registration = registration,
				Name = GameData.NameOf(registration),
				AddressText = GameData.AddressText(registration),
				IsFactory = isFactory,
			};
			foreach (ItemInstance instance in registration.itemInstances.Values)
			{
				Item item = ItemsGetter.GetByName(instance.itemName);
				if (item != null && item.HasTag(TagRef.Itemtag.iswarehousestorage))
				{
					place.MaxBoxes += item.cargoCapacity;
					place.UsedBoxes += instance.cargoInstances.Count;
				}
			}
			var inbound = new Dictionary<string, int>();
			var outbound = new Dictionary<string, int>();
			GameData.SumDeliveries(registration, _data.Day - 6, _data.Day, inbound, outbound);
			foreach (string product in registration.GetProducts())
			{
				inbound.TryGetValue(product, out int in7);
				outbound.TryGetValue(product, out int out7);
				place.Stock.Add(new StockLine
				{
					Item = product,
					Name = GameData.Localize(product),
					Stock = BuildingHelper.CountResourcesInPallets(registration.Address, product),
					In7 = in7,
					Out7 = out7,
				});
			}
			place.Stock.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
			return place;
		}

		private void BuildFactory(PlaceView place)
		{
			var registration = place.Registration;
			var groups = new Dictionary<string, MachineGroup>();
			var outputs = new List<string>();
			foreach (ItemInstance instance in registration.itemInstances.Values)
			{
				if (!(instance is FactoryWorkstationInstance workstation)) continue;
				Recipe recipe;
				try
				{
					recipe = workstation.SelectedRecipe;
				}
				catch
				{
					recipe = null;
				}
				if (recipe == null) continue;

				int[] hoursByDay = new int[7];
				float weeklyOutput = 0f;
				if (registration.scheduleDays != null)
				{
					foreach (ScheduleDay day in registration.scheduleDays)
					{
						if (day?.workShifts == null) continue;
						foreach (WorkShift shift in day.workShifts)
						{
							if (shift.itemInstanceId != workstation.id || string.IsNullOrEmpty(shift.employeeId)) continue;
							int hours = shift.endingHour - shift.startingHour;
							if (hours <= 0) hours += 24;
							hoursByDay[((int)day.day % 7 + 7) % 7] += hours;
							var employee = EmployeeHelper.GetEmployeeById(shift.employeeId, showError: false);
							float skill = employee != null ? employee.GetSkillValue("ba:skill_factoryworker") : 0f;
							weeklyOutput += recipe.GetScaledOutputAmount(skill) * hours;
						}
					}
				}
				int workDays = hoursByDay.Count(h => h > 0);
				int weeklyHours = hoursByDay.Sum();
				bool same = hoursByDay.Where(h => h > 0).Distinct().Count() <= 1;
				string key = workstation.itemName + "|" + recipe.id + "|" + string.Join(",", hoursByDay);
				if (!groups.TryGetValue(key, out var group))
				{
					group = new MachineGroup
					{
						Name = GameData.Localize(workstation.itemName),
						WorkDays = workDays,
						WeeklyHours = weeklyHours,
						SameHoursEveryDay = same,
						Inputs = recipe.ingredients ?? new List<RecipeItem>(),
						OutputItem = recipe.output.item,
					};
					groups.Add(key, group);
				}
				group.Count++;
				group.WeeklyOutput += weeklyOutput;
				if (!outputs.Contains(recipe.output.item)) outputs.Add(recipe.output.item);
				_producedByOwnFactories.Add(recipe.output.item);
			}
			place.Machines = groups.Values.OrderBy(g => g.Name).ToList();

			var need = new List<KeyValuePair<string, int>>();
			foreach (var group in place.Machines)
			{
				foreach (var input in group.Inputs)
				{
					int amount = input.amount * group.WeeklyHours * group.Count;
					int index = need.FindIndex(x => x.Key == input.item);
					if (index < 0) need.Add(new KeyValuePair<string, int>(input.item, amount));
					else need[index] = new KeyValuePair<string, int>(input.item, need[index].Value + amount);
				}
			}
			place.WeeklyNeed = need.Where(x => x.Value > 0).ToList();

			// 昨天：生产量来自 mod 记账，出口量和收入来自游戏的出口记录，运出来自配送记录
			int yesterday = _data.Day - 1;
			var inbound = new Dictionary<string, int>();
			var outbound = new Dictionary<string, int>();
			GameData.SumDeliveries(registration, yesterday, yesterday, inbound, outbound);
			place.HasLedger = Ledger.TryGetStockChange(registration, yesterday, out var change);
			var exports = registration.orderHistory?.LastOrDefault(x => x.dayNumber == yesterday)?.itemSales;
			foreach (string item in outputs)
			{
				outbound.TryGetValue(item, out int shipped);
				inbound.TryGetValue(item, out int received);
				var sale = exports?.FirstOrDefault(x => x.itemName == item);
				int exported = sale?.amountSold ?? 0;
				var line = new YesterdayLine
				{
					Name = GameData.Localize(item),
					Exported = exported,
					Revenue = sale?.totalPrice ?? 0f,
					Own = Math.Max(0, shipped - exported),
				};
				if (place.HasLedger)
				{
					change.TryGetValue(item, out int delta);
					line.Produced = Math.Max(0, delta + shipped - received);
				}
				place.Yesterday.Add(line);
			}
		}

		private void BuildWarehouse(PlaceView place)
		{
			var registration = place.Registration;
			var address = registration.Address;

			// 码头进口
			foreach (var contract in _save.importPartnerships ?? new List<ImportPartnership>())
			{
				var products = contract.products.Where(p => p.amount > 0 && p.assignedWarehouse == address).ToList();
				if (products.Count == 0) continue;
				string schedule;
				if (!contract.isActive) schedule = L.T("合同已暂停，不会送货", "Contract paused, no deliveries");
				else if (contract.isRepeatingOrder) schedule = L.F("每周一 8 点送货 · 下次第 {0} 天", "Delivered Mondays 8:00 · next on day {0}", contract.nextDeliveryDay);
				else schedule = L.F("一次性订单 · 第 {0} 天送到", "One-time order · arrives day {0}", contract.nextDeliveryDay);
				var card = new SourceCard
				{
					Kind = L.T("码头进口", "Import"),
					Title = GameData.BusinessName(contract.importAddress),
					Schedule = schedule,
					Unit = contract.isTarget ? L.T("每周补货到", "Weekly fill-up to") : L.T("每周进货量", "Per week"),
				};
				foreach (var product in products) card.Items.Add(new KeyValuePair<string, int>(GameData.Localize(product.itemName), product.amount));
				place.Sources.Add(card);
			}

			// 批发商送货合同
			foreach (var contract in _save.DeliveryContracts ?? new List<DeliveryContract>())
			{
				if (contract.businessAddress != address || contract.items == null) continue;
				var wholesaleItems = contract.items.Where(i => i.amount > 0).ToList();
				if (wholesaleItems.Count == 0) continue;
				var card = new SourceCard
				{
					Kind = L.T("批发进货", "Wholesale"),
					Title = GameData.BusinessName(contract.wholesaleAddress),
					Schedule = !contract.enabled ? L.T("合同已暂停，不会送货", "Contract paused, no deliveries") : contract.repeatingOrder ? L.F("每周一送货 · 下次第 {0} 天", "Delivered Mondays · next on day {0}", contract.nextDeliveryDay) : L.F("一次性订单 · 第 {0} 天送到", "One-time order · arrives day {0}", contract.nextDeliveryDay),
					Unit = L.T("每周进货量", "Per week"),
				};
				foreach (var item in wholesaleItems) card.Items.Add(new KeyValuePair<string, int>(GameData.Localize(item.itemName), item.amount));
				place.Sources.Add(card);
			}

			// 物流转入：别的工厂或仓库的配送线送到这里
			var transferredItems = new HashSet<string>();
			foreach (var plan in _plans)
			{
				if (plan.targetAddress == null || plan.targetAddress == address) continue;
				foreach (var destination in plan.destinations)
				{
					if (destination.deliveryTargetAddress != address) continue;
					var card = new SourceCard
					{
						Kind = L.T("物流转入", "Transfer in"),
						IsTransfer = true,
						Title = GameData.BusinessName(plan.targetAddress) + L.T(" → 本仓库", " → this warehouse"),
						Schedule = string.IsNullOrEmpty(plan.assignedEmployeeId) ? L.T("这条配送线没有物流经理，不会送货", "No logistics manager on this route, nothing is delivered") : L.T("物流经理每天凌晨 2 点送", "Logistics manager delivers daily at 2:00"),
						Unit = L.T("近 7 天转入", "Last 7 days"),
					};
					foreach (var target in destination.stockTargets.Where(t => t.targetAmount > 0))
					{
						var line = place.Stock.FirstOrDefault(s => s.Item == target.itemName);
						card.Items.Add(new KeyValuePair<string, int>(GameData.Localize(target.itemName), line?.In7 ?? 0));
						transferredItems.Add(target.itemName);
					}
					if (card.Items.Count > 0) place.Sources.Add(card);
				}
			}

			// 送货去向：先送工厂，再送商铺
			var items = new List<string>();
			void AddItem(string item)
			{
				if (!items.Contains(item)) items.Add(item);
			}
			foreach (var line in place.Stock) AddItem(line.Item);
			foreach (var plan in PlansFrom(address))
			{
				foreach (var destination in plan.destinations)
				{
					foreach (var target in destination.stockTargets.Where(t => t.targetAmount > 0)) AddItem(target.itemName);
				}
			}
			foreach (string item in items)
			{
				var balance = new BalanceLine { Item = item, Name = GameData.Localize(item) };
				if (transferredItems.Contains(item))
				{
					balance.InWeekly = place.Stock.FirstOrDefault(s => s.Item == item)?.In7 ?? 0;
				}
				else
				{
					balance.InWeekly = ImportsInto(address, item).Where(x => x.contract.isRepeatingOrder).Sum(x => x.product.amount) + WholesaleInto(address, item);
				}
				foreach (var plan in PlansFrom(address))
				{
					foreach (var destination in plan.destinations)
					{
						if (!destination.stockTargets.Any(t => t.itemName == item && t.targetAmount > 0)) continue;
						var target = BuildingHelper.GetBuildingRegistration(destination.deliveryTargetAddress);
						if (target == null) continue;
						var factory = _data.Factories.FirstOrDefault(f => f.Registration.Address == destination.deliveryTargetAddress);
						if (factory != null)
						{
							int need = factory.WeeklyNeed.FirstOrDefault(x => x.Key == item).Value;
							balance.Factories.Add(new KeyValuePair<string, int>(factory.Name, need));
							continue;
						}
						var store = _data.Stores.FirstOrDefault(s => s.Registration.Address == destination.deliveryTargetAddress);
						if (store != null)
						{
							int weekly = (int)Math.Round(StoreSales(store.Registration, item, out _) );
							balance.Stores.Add(new KeyValuePair<string, int>(store.Name, weekly));
						}
					}
				}
				if (balance.InWeekly == 0 && balance.Factories.Count == 0 && balance.Stores.Count == 0) continue;
				place.Balance.Add(balance);
			}
		}

		private StoreView BuildStore(BuildingRegistration registration)
		{
			var view = new StoreView
			{
				Registration = registration,
				Name = GameData.NameOf(registration),
				AddressText = GameData.AddressText(registration),
			};
			var suppliers = new List<string>();
			foreach (var plan in _plans)
			{
				if (plan.isFactory || plan.targetAddress == null) continue;
				foreach (var destination in plan.destinations)
				{
					if (destination.deliveryTargetAddress != registration.Address) continue;
					string supplier = GameData.BusinessName(plan.targetAddress);
					if (!suppliers.Contains(supplier)) suppliers.Add(supplier);
					foreach (var target in destination.stockTargets)
					{
						if (target.targetAmount <= 0) continue;
						var existing = view.Lines.FirstOrDefault(l => l.Item == target.itemName);
						if (existing != null)
						{
							existing.Targets.Add(target);
							continue;
						}
						float weekly = StoreSales(registration, target.itemName, out int peak);
						view.Lines.Add(new StoreLine
						{
							Item = target.itemName,
							Targets = new List<ItemAmountTarget> { target },
							Name = GameData.Localize(target.itemName),
							Stock = BuildingHelper.CountTotalResourcesInStock(registration.Address, target.itemName),
							Avg = weekly / 7f,
							Peak = peak,
							Target = target.targetAmount,
						});
					}
				}
			}
			view.Supplier = string.Join(L.Sep, suppliers);
			return view;
		}

		// 每个仓库每周要进的货：来自送货去向（工厂原料 + 商铺销量），再加上没有仓库负责送的工厂原料
		private void BuildSupplyRows()
		{
			foreach (var place in _data.Warehouses)
			{
				foreach (var line in place.Balance)
				{
					var row = new SupplyRow
					{
						Key = Ledger.Key(place.Registration) + ":" + line.Item,
						Item = line.Item,
						Name = line.Name,
						Warehouse = place.Registration,
						WarehouseName = place.Name,
						Dests = line.Factories.Where(f => f.Value > 0).ToList(),
						Stores = line.Stores.ToList(),
					};
					row.Need = row.Dests.Sum(x => x.Value) + row.Stores.Sum(x => x.Value);
					FillSupply(row);
					if (row.Need > 0 || row.Kind == SupplyKind.Import) _data.SupplyRows.Add(row);
				}
			}
			foreach (var factory in _data.Factories)
			{
				foreach (var need in factory.WeeklyNeed)
				{
					if (SupplierOf(factory.Registration.Address, need.Key) != null) continue;
					string key = "none:" + need.Key;
					var row = _data.SupplyRows.FirstOrDefault(r => r.Key == key);
					if (row == null)
					{
						row = new SupplyRow { Key = key, Item = need.Key, Name = GameData.Localize(need.Key), WarehouseName = L.T("没有仓库送", "No warehouse delivers") };
						_data.SupplyRows.Add(row);
					}
					row.Need += need.Value;
					row.Dests.Add(new KeyValuePair<string, int>(factory.Name, need.Value));
					FillSupply(row);
				}
			}
			// 每个仓库里原料排在前面
			_data.SupplyRows = _data.SupplyRows.OrderBy(r => r.Warehouse == null).ThenBy(r => r.WarehouseName).ThenBy(r => !r.IsRaw).ThenBy(r => r.Name).ToList();
		}

		private void FillSupply(SupplyRow row)
		{
			if (row.Warehouse == null)
			{
				row.Kind = SupplyKind.None;
				row.SupplyNote = L.T("没有仓库的配送线给工厂送这种原料，先在物流经理里加上", "No warehouse route delivers this to the factory. Add it in a logistics manager plan first");
				row.Supplied = 0;
				return;
			}
			var address = row.Warehouse.Address;
			var imports = ImportsInto(address, row.Item).ToList();
			var repeating = imports.Where(x => x.contract.isRepeatingOrder).ToList();
			int wholesale = WholesaleInto(address, row.Item);
			if (repeating.Count > 0 || wholesale > 0)
			{
				row.Kind = repeating.Count > 0 ? SupplyKind.Import : SupplyKind.Wholesale;
				row.Supplied = repeating.Sum(x => x.product.amount) + wholesale;
				string names = string.Join(L.Sep, repeating.Select(x => GameData.BusinessName(x.contract.importAddress)).Distinct());
				if (wholesale > 0) names = (names.Length > 0 ? names + " + " : "") + L.T("批发 ", "wholesale ") + GameData.N(wholesale);
				bool target = repeating.Any(x => x.contract.isTarget);
				row.SupplyNote = names + (target ? L.T(" · 每周补货到这个量", " · weekly fill-up") : "") + (row.Need == 0 ? L.T(" · 没有去处", " · nothing uses it") : row.Diff < 0 ? L.T(" · 每周少 ", " · short by ") + GameData.N(-row.Diff) : "");
				return;
			}
			if (_producedByOwnFactories.Contains(row.Item))
			{
				row.Kind = SupplyKind.Internal;
				var warehouse = _data.Warehouses.FirstOrDefault(w => w.Registration == row.Warehouse);
				row.Supplied = warehouse?.Stock.FirstOrDefault(s => s.Item == row.Item)?.In7 ?? 0;
				row.SupplyNote = L.F("自家工厂生产，近 7 天转入 {0}，不用下单", "Made by your factory, {0} transferred in last 7 days, no order needed", GameData.N(row.Supplied));
				return;
			}
			row.Kind = SupplyKind.None;
			row.SupplyNote = imports.Count > 0
				? L.T("只有一次性订单，没有每周进货", "Only one-time orders, no weekly supply")
				: L.F("{0}没有这种货的每周进货", "{0} has no weekly supply of this", row.WarehouseName);
		}

		private void BuildTotals()
		{
			var totals = new Dictionary<string, (int warehouse, int factory)>();
			foreach (var place in _data.Warehouses.Concat(_data.Factories))
			{
				foreach (var line in place.Stock)
				{
					totals.TryGetValue(line.Name, out var t);
					if (place.IsFactory) t.factory += line.Stock;
					else t.warehouse += line.Stock;
					totals[line.Name] = t;
				}
			}
			_data.Totals = totals.OrderBy(x => x.Key).Select(x => (x.Key, x.Value.warehouse, x.Value.factory)).ToList();
		}

		// 哪个仓库的配送线在给这个工厂送这种原料
		private Entities.Warehouse SupplierOf(Address factory, string item)
		{
			foreach (var plan in _plans)
			{
				if (plan.isFactory || plan.targetAddress == null) continue;
				foreach (var destination in plan.destinations)
				{
					if (destination.deliveryTargetAddress != factory) continue;
					if (!destination.stockTargets.Any(t => t.itemName == item && t.targetAmount > 0)) continue;
					if (BuildingHelper.GetBuildingRegistration(plan.targetAddress) is Entities.Warehouse warehouse && warehouse.RentedByPlayer) return warehouse;
				}
			}
			return null;
		}

		private IEnumerable<LogisticsManagerPlan> PlansFrom(Address source)
		{
			return _plans.Where(p => p.targetAddress != null && p.targetAddress == source);
		}

		private IEnumerable<(ImportPartnership contract, ImportProduct product)> ImportsInto(Address warehouse, string item)
		{
			foreach (var contract in _save.importPartnerships ?? new List<ImportPartnership>())
			{
				if (!contract.isActive) continue;
				foreach (var product in contract.products)
				{
					if (product.itemName == item && product.amount > 0 && product.assignedWarehouse == warehouse) yield return (contract, product);
				}
			}
		}

		// 批发商每周一送到这个仓库的量（只算启用中的每周合同）
		private int WholesaleInto(Address warehouse, string item)
		{
			return (_save.DeliveryContracts ?? new List<DeliveryContract>())
				.Where(c => c.enabled && c.repeatingOrder && c.businessAddress == warehouse && c.items != null)
				.SelectMany(c => c.items)
				.Where(i => i.itemName == item)
				.Sum(i => i.amount);
		}

		// 近 7 天卖出总量，peak = 单日最多
		private float StoreSales(BuildingRegistration store, string item, out int peak)
		{
			peak = 0;
			int total = 0;
			if (store.orderHistory == null) return 0f;
			foreach (var entry in store.orderHistory)
			{
				if (entry.dayNumber < _data.Day - 7 || entry.dayNumber > _data.Day - 1 || entry.itemSales == null) continue;
				int sold = entry.itemSales.Where(s => s.itemName == item).Sum(s => s.amountSold);
				total += sold;
				if (sold > peak) peak = sold;
			}
			return total;
		}

		private IEnumerable<BuildingRegistration> StoreRegistrations()
		{
			var seen = new HashSet<string>();
			foreach (var plan in _plans)
			{
				if (plan.isFactory || plan.targetAddress == null) continue;
				foreach (var destination in plan.destinations)
				{
					var registration = BuildingHelper.GetBuildingRegistration(destination.deliveryTargetAddress);
					if (registration == null || !registration.RentedByPlayer) continue;
					string type = registration.businessTypeName;
					if (type == GameData.WarehouseType || type == GameData.FactoryType || type == GameData.ExporterType) continue;
					if (seen.Add(Ledger.Key(registration))) yield return registration;
				}
			}
		}

		private static T Safe<T>(Func<T> action) where T : class
		{
			try
			{
				return action();
			}
			catch (Exception ex)
			{
				TerminalMod.LogWarning("Failed to read data: " + ex.Message);
				return null;
			}
		}

		private static void Try(Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				TerminalMod.LogWarning("Failed to read data: " + ex);
			}
		}
	}
}

public static class GameData
{
	public const string WarehouseType = "ba:businesstype_warehouse";
	public const string FactoryType = "ba:businesstype_factory";
	public const string ExporterType = "ba:businesstype_importexport";

	public static IEnumerable<Entities.Warehouse> PlayerWarehouses() => PlayerOfType(WarehouseType);

	public static IEnumerable<Entities.Warehouse> PlayerFactories() => PlayerOfType(FactoryType);

	// 工厂在存档里也是 Entities.Warehouse
	private static IEnumerable<Entities.Warehouse> PlayerOfType(string type)
	{
		var save = SaveGameManager.Current;
		if (save == null) return Enumerable.Empty<Entities.Warehouse>();
		return save.BuildingRegistrations
			.Where(r => r.RentedByPlayer && r.businessTypeName == type)
			.OfType<Entities.Warehouse>()
			.OrderBy(r => r.BusinessName)
			.ToList();
	}

	// 某段日子里（含首尾）每种货物运进 / 运出了多少
	public static void SumDeliveries(Entities.Warehouse registration, int fromDay, int toDay, Dictionary<string, int> inbound, Dictionary<string, int> outbound)
	{
		if (registration.deliveryTransactions == null) return;
		foreach (var transaction in registration.deliveryTransactions)
		{
			if (transaction.dayOfDelivery < fromDay || transaction.dayOfDelivery > toDay) continue;
			foreach (var item in transaction.deliveryItems)
			{
				var target = item.amountDelivered >= 0 ? inbound : outbound;
				target.TryGetValue(item.itemName, out int sum);
				target[item.itemName] = sum + Math.Abs(item.amountDelivered);
			}
		}
	}

	public static string NameOf(BuildingRegistration registration)
	{
		return string.IsNullOrEmpty(registration.BusinessName) ? L.T("(未命名)", "(unnamed)") : registration.BusinessName;
	}

	public static string BusinessName(Address address)
	{
		var registration = address == null ? null : BuildingHelper.GetBuildingRegistration(address);
		return registration == null ? L.T("(未知)", "(unknown)") : NameOf(registration);
	}

	public static string AddressText(BuildingRegistration registration)
	{
		try
		{
			return registration.Address.ToFormattedString();
		}
		catch
		{
			return registration.StreetNumber + " " + registration.StreetName;
		}
	}

	public static string N(float value) => Math.Round(value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

	public static string Localize(string key)
	{
		try
		{
			string text = key.GetLocalization();
			return string.IsNullOrEmpty(text) ? key : text;
		}
		catch
		{
			return key;
		}
	}
}
