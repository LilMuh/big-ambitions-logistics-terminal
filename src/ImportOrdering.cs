using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Buildings;
using Entities;
using Helpers;

namespace LogisticsTerminal;

// 一次要改的一行采购合同
public sealed class ImportChange
{
	public SupplyRow Row;
	public ImportPartnership Contract;
	public string ImporterName;
	public int OldAmount;
	public int NewAmount;
}

// 下单 / 加量 / 一键调整都先算出这样一份计划，玩家确认后再改合同
public sealed class ImportPlan
{
	public List<ImportChange> Changes = new List<ImportChange>();
	public List<string> Problems = new List<string>();
	// 会被启用的暂停合同，以及里面其它会一起开始送的货
	public List<string> Activations = new List<string>();
}

// 「下单」= 在已有的采购合同（进口合作）里设好这种货每周进多少、送到哪个仓库，并保证合同每周都送。
// 新建进口合作需要打电话给进口商并指定一名空闲的采购员，mod 不替玩家做这一步。
public static class ImportOrdering
{
	public static int RoundUp(int amount) => (int)Math.Ceiling(amount / 10f) * 10;

	// 把这些行的每周进货调整到每周需要（向上取整到 10）。onlyIncrease: 只补不够的，不减多的
	public static ImportPlan Plan(IEnumerable<SupplyRow> rows, bool onlyIncrease)
	{
		var plan = new ImportPlan();
		var save = SaveGameManager.Current;
		if (save == null) return plan;
		var contracts = save.importPartnerships ?? new List<ImportPartnership>();
		var activated = new HashSet<ImportPartnership>();
		foreach (var row in rows)
		{
			if (row.Warehouse == null)
			{
				plan.Problems.Add(row.Name + L.T("：没有仓库的配送线给工厂送，先在物流经理里加上", ": no warehouse route delivers this to the factory. Add it in a logistics manager plan first"));
				continue;
			}
			// 自家工厂供货、只有批发合同的不改
			if (row.Kind == SupplyKind.Internal || row.Kind == SupplyKind.Wholesale || row.Need <= 0) continue;
			var warehouse = row.Warehouse.Address;
			int target = RoundUp(row.Need);
			// 已有批发合同的量也算在里面，只用码头进口补差额
			int current = row.Supplied;
			int delta = target - current;
			if (delta == 0 || (onlyIncrease && delta < 0)) continue;

			var existing = contracts
				.Where(c => c.isActive && c.isRepeatingOrder)
				.SelectMany(c => c.products.Where(p => p.itemName == row.Item && p.amount > 0 && p.assignedWarehouse == warehouse).Select(p => (c, p)))
				.ToList();

			if (delta < 0)
			{
				// 从最后一份合同开始减
				for (int i = existing.Count - 1; i >= 0 && delta < 0; i--)
				{
					var (contract, product) = existing[i];
					int newAmount = Math.Max(0, product.amount + delta);
					delta += product.amount - newAmount;
					plan.Changes.Add(Change(row, contract, product.amount, newAmount));
				}
				continue;
			}

			// 先在已有的合同里加，加到进口商的每周上限为止
			foreach (var (contract, product) in existing)
			{
				if (delta <= 0) break;
				int newAmount = Math.Min(product.amount + delta, Cap(contract, row.Item));
				if (newAmount <= product.amount) continue;
				delta -= newAmount - product.amount;
				plan.Changes.Add(Change(row, contract, product.amount, newAmount));
			}
			// 不够再找别的卖这种货的合同
			var used = new HashSet<ImportPartnership>(existing.Select(x => x.c));
			while (delta > 0)
			{
				var contract = contracts
					.Where(c => !used.Contains(c) && HasAgent(c) && Sells(c, row.Item) && IsSlotFree(c, row.Item, warehouse))
					.OrderByDescending(c => c.isActive && c.isRepeatingOrder)
					.ThenByDescending(IsRawMaterialsImporter)
					.FirstOrDefault();
				if (contract == null) break;
				used.Add(contract);
				int newAmount = Math.Min(delta, Cap(contract, row.Item));
				if (newAmount <= 0) continue;
				delta -= newAmount;
				plan.Changes.Add(Change(row, contract, 0, newAmount));
				if (!contract.isActive && activated.Add(contract))
				{
					var others = contract.products.Where(p => p.amount > 0 && p.itemName != row.Item)
						.Select(p => GameData.Localize(p.itemName) + " " + GameData.N(p.amount)).ToList();
					plan.Activations.Add(L.F("「{0}」的合同现在是暂停的，会被启用，下周一开始送货", "The contract with \"{0}\" is paused and will be activated, deliveries start next Monday", GameData.BusinessName(contract.importAddress))
						+ (others.Count > 0 ? L.F("（合同里的 {0} 也会一起送）", " (its {0} will be delivered too)", string.Join(L.Sep, others)) : ""));
				}
			}
			if (delta > 0)
			{
				plan.Problems.Add(row.WarehouseName + " · " + row.Name + L.T("：还差每周 ", ": still short ") + GameData.N(delta) + L.T("", " per week")
					+ (existing.Count > 0 || plan.Changes.Any(c => c.Row == row)
						? L.T("，进口商每周卖到上限了，需要再和一家卖这种货的进口商建立合作", ". The importer's weekly limit is reached; partner with another importer that sells it")
						: L.T("，没有能进这种货的采购合同。先打电话给卖这种货的进口商建立合作（总部需要一名空闲的采购员）", ". No purchasing contract can buy this. Call an importer that sells it to start a partnership first (needs a free purchasing agent at HQ)")));
			}
		}
		return plan;
	}

	public static string Apply(ImportPlan plan)
	{
		int done = 0;
		foreach (var change in plan.Changes)
		{
			try
			{
				ApplyChange(change);
				done++;
			}
			catch (Exception ex)
			{
				TerminalMod.LogError("Failed to change contract: " + change.Row.Item + " " + ex);
			}
		}
		SaveGameManager.MarkChange();
		TerminalMod.Log("Adjusted imports: " + done + " changes");
		return L.F("已调整 {0} 项每周进货，下周一按新数量送货", "Changed {0} weekly import amounts, new amounts arrive next Monday", done);
	}

	private static void ApplyChange(ImportChange change)
	{
		var contract = change.Contract;
		var warehouse = change.Row.Warehouse.Address;
		try
		{
			contract.AddMissingImportProducts();
		}
		catch (Exception ex)
		{
			TerminalMod.LogWarning("Failed to fill contract products: " + ex.Message);
		}
		string item = change.Row.Item;
		var product = contract.products.FirstOrDefault(p => p.itemName == item && p.amount > 0 && p.assignedWarehouse == warehouse)
			?? contract.products.FirstOrDefault(p => p.itemName == item && (p.amount == 0 || p.assignedWarehouse == null || p.assignedWarehouse == warehouse));
		if (product == null)
		{
			product = new ImportProduct { itemName = item };
			contract.products.Add(product);
		}
		product.assignedWarehouse = warehouse;
		product.amount = change.NewAmount;
		if (change.NewAmount > 0)
		{
			if (!contract.isActive)
			{
				contract.isActive = true;
				contract.nextDeliveryDay = DeliveryHelper.GetNextDeliveryDay();
			}
			contract.isRepeatingOrder = true;
		}
	}

	private static ImportChange Change(SupplyRow row, ImportPartnership contract, int oldAmount, int newAmount)
	{
		return new ImportChange
		{
			Row = row,
			Contract = contract,
			ImporterName = GameData.BusinessName(contract.importAddress),
			OldAmount = oldAmount,
			NewAmount = newAmount,
		};
	}

	// 进口商对这种货的每周上限（原料进口商卖工厂原料没有上限）
	private static int Cap(ImportPartnership contract, string item)
	{
		if (contract.isTarget || DeliveryHelper.AreWholesaleAndImportLimitsDisabled()) return int.MaxValue;
		if (!DeliveryHelper.ShouldLimitImporterMaxAmount(item, contract.importAddress)) return int.MaxValue;
		return ItemsGetter.GetByName(item).maxOrderAmountPerImporter;
	}

	private static bool HasAgent(ImportPartnership contract)
	{
		try
		{
			return contract.PurchasingAgentInstance != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool Sells(ImportPartnership contract, string item)
	{
		try
		{
			var settings = BuildingHelper.GetBuilding(contract.importAddress)?.SpecialService?.settings as ImportExportSettings;
			if (settings != null) return settings.GetItemsAvailable().Contains(item);
		}
		catch
		{
		}
		return contract.products.Any(p => p.itemName == item);
	}

	// 一份合同里每种货只有一行、只能送一个仓库：这一行要么没用，要么已经是送这个仓库
	private static bool IsSlotFree(ImportPartnership contract, string item, Address warehouse)
	{
		var product = contract.products.FirstOrDefault(p => p.itemName == item);
		return product == null || product.amount == 0 || product.assignedWarehouse == null || product.assignedWarehouse == warehouse;
	}

	private static bool IsRawMaterialsImporter(ImportPartnership contract)
	{
		try
		{
			return BuildingHelper.GetBuilding(contract.importAddress).SpecialService.isRawMaterialsImporter;
		}
		catch
		{
			return false;
		}
	}
}

// 商铺补货目标一键按建议改：建议 = 近 7 天单日最多卖 × 2
public static class StoreRestock
{
	public sealed class Change
	{
		public StoreView Store;
		public StoreLine Line;
	}

	// 近 7 天没卖出的商品不改（可能是新上架）
	public static List<Change> Plan(IEnumerable<StoreView> stores)
	{
		return stores.SelectMany(s => s.Lines.Where(l => l.Peak > 0 && l.Target != l.Suggest).Select(l => new Change { Store = s, Line = l })).ToList();
	}

	public static string Apply(List<Change> changes)
	{
		foreach (var change in changes)
		{
			foreach (var target in change.Line.Targets) target.targetAmount = change.Line.Suggest;
		}
		SaveGameManager.MarkChange();
		int stores = changes.Select(c => c.Store).Distinct().Count();
		TerminalMod.Log("Applied suggested restock targets: " + stores + " stores, " + changes.Count + " items");
		return L.F("已改 {0} 家店、{1} 项补货目标，今晚凌晨 2 点按新目标补货", "Changed {1} restock targets in {0} stores, applied at the 2:00 delivery", stores, changes.Count);
	}
}
