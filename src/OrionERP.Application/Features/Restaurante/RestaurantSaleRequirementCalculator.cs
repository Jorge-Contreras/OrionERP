namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantSaleRequirementGraph
{
  public IReadOnlyDictionary<int, RestaurantSaleMaterialNode> Materials { get; init; }
    = new Dictionary<int, RestaurantSaleMaterialNode>();
  public IReadOnlyDictionary<int, RestaurantSaleBomNode> ActiveBoms { get; init; }
    = new Dictionary<int, RestaurantSaleBomNode>();
  public IReadOnlyList<RestaurantSaleUnitConversionNode> UnitConversions { get; init; } = [];
  public IReadOnlyList<RestaurantSaleModifierDeltaNode> ModifierDeltas { get; init; } = [];
}

public sealed class RestaurantSaleMaterialNode
{
  public int Id { get; init; }
  public string Code { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public string FulfillmentMode { get; init; } = string.Empty;
  public int BaseUnitId { get; init; }
  public string BaseUnit { get; init; } = string.Empty;
  public bool TrackLots { get; init; }
  public bool IsActive { get; init; }
}

public sealed class RestaurantSaleBomNode
{
  public long VersionId { get; init; }
  public int VersionNumber { get; init; }
  public int ProductMaterialId { get; init; }
  public decimal YieldQuantity { get; init; }
  public int YieldUnitId { get; init; }
  public string YieldUnit { get; init; } = string.Empty;
  public IReadOnlyList<RestaurantSaleBomComponentNode> Components { get; init; } = [];
}

public sealed class RestaurantSaleBomComponentNode
{
  public long Id { get; init; }
  public int MaterialId { get; init; }
  public decimal Quantity { get; init; }
  public int UnitId { get; init; }
  public string Unit { get; init; } = string.Empty;
  public decimal ExpectedWastePercent { get; init; }
  public int SortOrder { get; init; }
}

public sealed class RestaurantSaleUnitConversionNode
{
  public int? MaterialId { get; init; }
  public int FromUnitId { get; init; }
  public int ToUnitId { get; init; }
  public decimal Factor { get; init; }
}

public sealed class RestaurantSaleModifierDeltaNode
{
  public long OptionId { get; init; }
  public int MaterialId { get; init; }
  public string EffectKind { get; init; } = RestaurantModifierEffectKinds.AdjustQuantity;
  public decimal QuantityDelta { get; init; }
  public int? UnitId { get; init; }
  public string Unit { get; init; } = string.Empty;
}

public sealed class RestaurantSaleRequirementCalculation
{
  public IReadOnlyDictionary<int, decimal> Requirements { get; init; }
    = new Dictionary<int, decimal>();
  public IReadOnlyDictionary<int, string> RequirementPaths { get; init; }
    = new Dictionary<int, string>();
  public IReadOnlyDictionary<int, int> RequirementDepths { get; init; }
    = new Dictionary<int, int>();
  public IReadOnlyList<RestaurantSaleRequirementTrace> Trace { get; init; } = [];
  public IReadOnlyList<RestaurantSaleRequirementIssue> Issues { get; init; } = [];
}

public sealed class RestaurantSaleRequirementTrace
{
  public int Depth { get; init; }
  public string Path { get; init; } = string.Empty;
  public int ParentMaterialId { get; init; }
  public long? BomVersionId { get; init; }
  public int? BomVersionNumber { get; init; }
  public decimal? YieldQuantity { get; init; }
  public string YieldUnit { get; init; } = string.Empty;
  public int? ComponentMaterialId { get; init; }
  public decimal? ComponentQuantity { get; init; }
  public string ComponentUnit { get; init; } = string.Empty;
  public decimal? ExpectedWastePercent { get; init; }
  public decimal? ConversionFactor { get; init; }
  public decimal? RequiredBaseQuantity { get; init; }
  public string Status { get; init; } = RestaurantSaleReadinessStatuses.Ready;
  public string? Message { get; init; }
}

public sealed class RestaurantSaleRequirementIssue
{
  public string Code { get; init; } = string.Empty;
  public string Message { get; init; } = string.Empty;
  public int? MaterialId { get; init; }
  public string Path { get; init; } = string.Empty;
}

public static class RestaurantSaleRequirementCalculator
{
  private const int MaximumBomDepth = 32;

  /// <summary>
  /// Calcula el consumo de materiales de una venta. <paramref name="modifierOptionIds"/> es un multiconjunto:
  /// repetir un id significa que esa opción se pidió más de una vez y sus efectos se multiplican por igual.
  /// </summary>
  public static RestaurantSaleRequirementCalculation Calculate(
    RestaurantSaleRequirementGraph graph,
    int rootMaterialId,
    string rootProductSku,
    decimal quantity,
    IReadOnlyCollection<long>? modifierOptionIds = null)
  {
    ArgumentNullException.ThrowIfNull(graph);
    if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

    var state = new CalculationState(graph, rootProductSku);
    if (!graph.Materials.TryGetValue(rootMaterialId, out var rootMaterial))
    {
      state.AddIssue("MATERIAL_MISSING", $"El producto {rootProductSku} referencia el material inexistente {rootMaterialId}.", rootMaterialId, $"material {rootMaterialId}");
    }
    else if (!rootMaterial.IsActive)
    {
      state.AddIssue("MATERIAL_INACTIVE", $"El material {MaterialLabel(rootMaterial)} del producto {rootProductSku} está inactivo.", rootMaterial.Id, MaterialLabel(rootMaterial));
    }
    else if (string.Equals(rootMaterial.FulfillmentMode, "MakeToOrder", StringComparison.OrdinalIgnoreCase))
    {
      ExpandMaterial(state, rootMaterial, quantity, [], 0);
    }
    else
    {
      state.AddRequirement(rootMaterial, quantity, MaterialLabel(rootMaterial), 0);
    }

    if (modifierOptionIds is { Count: > 0 })
    {
      var selectedCounts = modifierOptionIds
        .GroupBy(optionId => optionId)
        .ToDictionary(group => group.Key, group => group.Count());
      var selectedEffects = graph.ModifierDeltas.Where(delta => selectedCounts.ContainsKey(delta.OptionId)).ToList();
      foreach (var effect in selectedEffects.Where(effect =>
                 RestaurantModifierEffectKinds.Normalize(effect.EffectKind) == RestaurantModifierEffectKinds.RemoveIngredient))
      {
        if (!graph.Materials.TryGetValue(effect.MaterialId, out var material))
        {
          state.AddIssue("MODIFIER_MATERIAL_MISSING", $"Un modificador referencia el material inexistente {effect.MaterialId}.", effect.MaterialId, $"Modificador > material {effect.MaterialId}");
          continue;
        }
        state.RemoveRequirement(material.Id);
      }

      foreach (var delta in selectedEffects.Where(effect =>
                 RestaurantModifierEffectKinds.Normalize(effect.EffectKind) != RestaurantModifierEffectKinds.RemoveIngredient))
      {
        if (!graph.Materials.TryGetValue(delta.MaterialId, out var material))
        {
          state.AddIssue("MODIFIER_MATERIAL_MISSING", $"Un modificador referencia el material inexistente {delta.MaterialId}.", delta.MaterialId, $"Modificador > material {delta.MaterialId}");
          continue;
        }
        if (!delta.UnitId.HasValue)
        {
          state.AddIssue("MODIFIER_UNIT_MISSING", "Falta la unidad para los ingredientes de un modificador.", material.Id, $"Modificador > {MaterialLabel(material)}");
          continue;
        }
        var factor = FindFactor(graph, material, delta.UnitId.Value);
        if (!factor.HasValue)
        {
          state.AddIssue("MODIFIER_CONVERSION_MISSING", "Falta una conversión para los ingredientes de un modificador.", material.Id, $"Modificador > {MaterialLabel(material)}");
          continue;
        }
        state.AddRequirement(material, delta.QuantityDelta * factor.Value * quantity * selectedCounts[delta.OptionId], $"Modificador > {MaterialLabel(material)}", 0);
      }
    }

    return state.ToResult();
  }

  public static decimal? FindConversionFactor(RestaurantSaleRequirementGraph graph, int materialId, int fromUnitId)
  {
    if (!graph.Materials.TryGetValue(materialId, out var material)) return null;
    return FindFactor(graph, material, fromUnitId);
  }

  private static void ExpandMaterial(
    CalculationState state,
    RestaurantSaleMaterialNode material,
    decimal multiplier,
    IReadOnlyList<int> path,
    int depth)
  {
    var displayPath = BuildPath(state.Graph, path.Append(material.Id));
    if (depth >= MaximumBomDepth || path.Contains(material.Id))
    {
      const string message = "El BOM contiene un ciclo o excede 32 niveles.";
      state.AddIssue("BOM_CYCLE_OR_DEPTH", message, material.Id, displayPath);
      state.Trace.Add(new RestaurantSaleRequirementTrace
      {
        Depth = depth,
        Path = displayPath,
        ParentMaterialId = material.Id,
        Status = RestaurantSaleReadinessStatuses.ConfigurationBlocked,
        Message = message
      });
      return;
    }

    if (!state.Graph.ActiveBoms.TryGetValue(material.Id, out var bom) || bom.Components.Count == 0)
    {
      var message = depth == 0
        ? $"El producto {state.RootProductSku} ({MaterialLabel(material)}) no tiene un BOM activo."
        : $"El ingrediente {MaterialLabel(material)} del producto {state.RootProductSku} está configurado para fabricación bajo pedido y no tiene un BOM activo.";
      state.AddIssue("BOM_MISSING", message, material.Id, displayPath);
      state.Trace.Add(new RestaurantSaleRequirementTrace
      {
        Depth = depth,
        Path = displayPath,
        ParentMaterialId = material.Id,
        BomVersionId = bom?.VersionId,
        BomVersionNumber = bom?.VersionNumber,
        YieldQuantity = bom?.YieldQuantity,
        YieldUnit = bom?.YieldUnit ?? string.Empty,
        Status = RestaurantSaleReadinessStatuses.ConfigurationBlocked,
        Message = message
      });
      return;
    }
    if (bom.YieldQuantity <= 0)
    {
      var message = $"El BOM del material {material.Id} tiene un rendimiento inválido.";
      state.AddIssue("BOM_INVALID_YIELD", message, material.Id, displayPath);
      state.Trace.Add(new RestaurantSaleRequirementTrace
      {
        Depth = depth,
        Path = displayPath,
        ParentMaterialId = material.Id,
        BomVersionId = bom.VersionId,
        BomVersionNumber = bom.VersionNumber,
        YieldQuantity = bom.YieldQuantity,
        YieldUnit = bom.YieldUnit,
        Status = RestaurantSaleReadinessStatuses.ConfigurationBlocked,
        Message = message
      });
      return;
    }

    var nextPath = path.Append(material.Id).ToArray();
    foreach (var component in bom.Components.OrderBy(component => component.SortOrder).ThenBy(component => component.Id))
    {
      if (!state.Graph.Materials.TryGetValue(component.MaterialId, out var componentMaterial))
      {
        var missingPath = $"{displayPath} > material {component.MaterialId}";
        var message = $"El BOM del producto {state.RootProductSku} referencia el material inexistente {component.MaterialId}.";
        state.AddIssue("BOM_COMPONENT_MISSING", message, component.MaterialId, missingPath);
        state.Trace.Add(Trace(bom, component, material.Id, depth, missingPath, null, null, RestaurantSaleReadinessStatuses.ConfigurationBlocked, message));
        continue;
      }
      if (!componentMaterial.IsActive)
      {
        var inactivePath = $"{displayPath} > {MaterialLabel(componentMaterial)}";
        var message = $"El material {MaterialLabel(componentMaterial)} del producto {state.RootProductSku} está inactivo.";
        state.AddIssue("BOM_COMPONENT_INACTIVE", message, componentMaterial.Id, inactivePath);
        state.Trace.Add(Trace(bom, component, material.Id, depth, inactivePath, null, null, RestaurantSaleReadinessStatuses.ConfigurationBlocked, message));
        continue;
      }

      var factor = FindFactor(state.Graph, componentMaterial, component.UnitId);
      var componentPath = $"{displayPath} > {MaterialLabel(componentMaterial)}";
      if (!factor.HasValue)
      {
        var message = $"Falta una conversión de unidad para el material {component.MaterialId}.";
        state.AddIssue("BOM_CONVERSION_MISSING", message, component.MaterialId, componentPath);
        state.Trace.Add(Trace(bom, component, material.Id, depth, componentPath, null, null, RestaurantSaleReadinessStatuses.ConfigurationBlocked, message));
        continue;
      }

      var required = component.Quantity
        * (1 + component.ExpectedWastePercent / 100m)
        * factor.Value
        / bom.YieldQuantity
        * multiplier;
      state.Trace.Add(Trace(bom, component, material.Id, depth, componentPath, factor, required, RestaurantSaleReadinessStatuses.Ready, null));

      if (string.Equals(componentMaterial.FulfillmentMode, "MakeToOrder", StringComparison.OrdinalIgnoreCase))
      {
        ExpandMaterial(state, componentMaterial, required, nextPath, depth + 1);
      }
      else
      {
        state.AddRequirement(componentMaterial, required, componentPath, depth + 1);
      }
    }
  }

  private static RestaurantSaleRequirementTrace Trace(
    RestaurantSaleBomNode bom,
    RestaurantSaleBomComponentNode component,
    int parentMaterialId,
    int depth,
    string path,
    decimal? factor,
    decimal? required,
    string status,
    string? message)
    => new()
    {
      Depth = depth,
      Path = path,
      ParentMaterialId = parentMaterialId,
      BomVersionId = bom.VersionId,
      BomVersionNumber = bom.VersionNumber,
      YieldQuantity = bom.YieldQuantity,
      YieldUnit = bom.YieldUnit,
      ComponentMaterialId = component.MaterialId,
      ComponentQuantity = component.Quantity,
      ComponentUnit = component.Unit,
      ExpectedWastePercent = component.ExpectedWastePercent,
      ConversionFactor = factor,
      RequiredBaseQuantity = required,
      Status = status,
      Message = message
    };

  private static decimal? FindFactor(RestaurantSaleRequirementGraph graph, RestaurantSaleMaterialNode material, int fromUnitId)
  {
    if (fromUnitId == material.BaseUnitId) return 1m;
    var materialFactor = graph.UnitConversions.FirstOrDefault(conversion =>
      conversion.MaterialId == material.Id && conversion.FromUnitId == fromUnitId && conversion.ToUnitId == material.BaseUnitId);
    if (materialFactor is not null) return materialFactor.Factor;
    return graph.UnitConversions.FirstOrDefault(conversion =>
      conversion.MaterialId is null && conversion.FromUnitId == fromUnitId && conversion.ToUnitId == material.BaseUnitId)?.Factor;
  }

  private static string BuildPath(RestaurantSaleRequirementGraph graph, IEnumerable<int> materialIds)
    => string.Join(" > ", materialIds.Select(id => graph.Materials.TryGetValue(id, out var material) ? MaterialLabel(material) : $"material {id}"));

  private static string MaterialLabel(RestaurantSaleMaterialNode material)
    => string.IsNullOrWhiteSpace(material.Name)
      ? $"material {material.Id}"
      : $"{material.Name} (material {material.Id})";

  private sealed class CalculationState
  {
    private readonly Dictionary<int, decimal> _requirements = [];
    private readonly Dictionary<int, string> _paths = [];
    private readonly Dictionary<int, int> _depths = [];

    public CalculationState(RestaurantSaleRequirementGraph graph, string rootProductSku)
    {
      Graph = graph;
      RootProductSku = string.IsNullOrWhiteSpace(rootProductSku) ? "sin SKU" : rootProductSku.Trim();
    }

    public RestaurantSaleRequirementGraph Graph { get; }
    public string RootProductSku { get; }
    public List<RestaurantSaleRequirementTrace> Trace { get; } = [];
    public List<RestaurantSaleRequirementIssue> Issues { get; } = [];

    public void AddRequirement(RestaurantSaleMaterialNode material, decimal quantity, string path, int depth)
    {
      _requirements[material.Id] = _requirements.GetValueOrDefault(material.Id) + quantity;
      if (!_paths.ContainsKey(material.Id)) _paths[material.Id] = path;
      if (!_depths.TryGetValue(material.Id, out var currentDepth) || depth > currentDepth) _depths[material.Id] = depth;
    }

    public void RemoveRequirement(int materialId)
    {
      _requirements.Remove(materialId);
      _paths.Remove(materialId);
      _depths.Remove(materialId);
    }

    public void AddIssue(string code, string message, int? materialId, string path)
      => Issues.Add(new RestaurantSaleRequirementIssue { Code = code, Message = message, MaterialId = materialId, Path = path });

    public RestaurantSaleRequirementCalculation ToResult()
    {
      var positiveRequirements = _requirements.Where(item => item.Value > 0)
        .ToDictionary(item => item.Key, item => item.Value);
      return new RestaurantSaleRequirementCalculation
      {
        Requirements = positiveRequirements,
        RequirementPaths = _paths.Where(item => positiveRequirements.ContainsKey(item.Key)).ToDictionary(),
        RequirementDepths = _depths.Where(item => positiveRequirements.ContainsKey(item.Key)).ToDictionary(),
        Trace = Trace,
        Issues = Issues
      };
    }
  }
}
