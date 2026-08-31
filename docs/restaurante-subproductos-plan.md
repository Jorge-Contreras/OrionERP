# Subproductos (semielaborados) en Restaurante — diagnóstico y plan

Fecha: 2026-08-30 · RFC analizado: `BRUNOS260707L26` (verificado en `grupocarpio` y `Orion_SandBox`)

---

## 0. Resumen

El sistema **sí** tiene la maquinaria completa para subrecetas (explosión multinivel, detección de ciclos,
rollup de costo, guardas de archivado, órdenes de producción). El problema no es de motor, es de
**clasificación**: la columna que decide el comportamiento (`FulfillmentMode`) y la que el usuario ve y
edita (`ProductType`) son independientes, nadie las valida entre sí, y **no existe pantalla para editarlas**
salvo el alta de un producto vendible en el POS.

El resultado en Bruno's hoy:

| Hecho verificado | Valor |
|---|---|
| Materiales con receta activa propia | 12 |
| …de esos, clasificados como `RawMaterial` | **11** |
| BOMs elegibles para orden de producción (`MakeToStock` + receta activa) | **0** |
| Órdenes de producción emitidas en la historia del RFC | **0** |
| Subproductos cuyo costo sale de la receta (no de `BaseUnitPrice`) | **0 de 12** |
| Subproductos usados en recetas activas con stock en 0 | 2 (`CURTIDO DE PEPINOS`, `HAMBURGUESA DE SIRLON`) |

Es decir: las recetas de subproducto existen, pero **ninguna se ejecuta ni se costea**. Son decorativas.

---

## 1. Cómo funciona hoy

### 1.1 Las dos columnas

`logistica.Material` tiene dos columnas agregadas en `20260713_restaurant_inventory_bom.sql`:

| Columna | Valores | Default | ¿Quién la lee? |
|---|---|---|---|
| `ProductType` | `RawMaterial`, `FinishedGood`, `SemiFinished`, (`Resale` en la UI, nunca en datos) | `RawMaterial` | **2 lugares**, ambos cosméticos salvo uno |
| `FulfillmentMode` | `StockItem`, `MakeToOrder`, `MakeToStock` | `StockItem` | **todo el motor** |

No hay `CHECK CONSTRAINT` sobre ninguna de las dos. Cualquier combinación es válida en la base.

### 1.2 `FulfillmentMode` es la verdad del comportamiento

| Modo | Al vender (POS) | En `/restaurante/produccion` | Significado real |
|---|---|---|---|
| `MakeToOrder` | **Explota** su BOM activo recursivamente | No producible | Subreceta *fantasma*: no tiene stock propio, se descuenta a materia prima cada venta |
| `MakeToStock` | Descuenta **su propio stock** | **Producible** (orden por lote) | Subproducto real: se produce por lote y vive en inventario |
| `StockItem` | Descuenta **su propio stock** | **No producible** | Insumo comprado |

Referencias:
- Venta: [`RestaurantSaleRequirementCalculator.cs:126`](../src/OrionERP.Application/Features/Restaurante/RestaurantSaleRequirementCalculator.cs:126) (raíz) y [`:262`](../src/OrionERP.Application/Features/Restaurante/RestaurantSaleRequirementCalculator.cs:262) (componentes).
- Producción: [`RestaurantProductionService.cs:52`](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantProductionService.cs:52) (catálogo) y [`:104`](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantProductionService.cs:104) (validación al planear); explosión en [`:315`](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantProductionService.cs:315).
- Guardas de receta: [`BomRecipeService.cs:778`](../src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs:778) (archivado), [`:1097`](../src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs:1097) y [`:1104`](../src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs:1104) (subreceta incompleta).

Nota crítica: **`StockItem` y `MakeToStock` se comportan idénticamente en el POS.** La única diferencia es
que `MakeToStock` habilita producción. Por eso el error es invisible hasta que alguien intenta producir.

### 1.3 `ProductType` casi no se lee — salvo donde rompe

Solo dos consumidores:

1. [`RestaurantMaterialOption.cs:47`](../src/OrionERP.Web/Features/Restaurante/RestaurantMaterialOption.cs:47) — etiqueta de grupo del combo ("Productos y subrecetas" vs. categoría). Cosmético.
2. [`RestaurantRecipesPage.razor:301-303`](../src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor:301) — **filtro del selector "Producto o preparación terminada"**. Aquí una columna decorativa se vuelve una barrera dura.

### 1.4 Nadie puede editar la clasificación

`UPDATE logistica.Material SET ProductType = …, FulfillmentMode = …` existe **una sola vez** en todo el
código: [`RestaurantCatalogService.cs:346`](../src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs:346), dentro de `SaveProductAsync`.

Consecuencias:

- Para clasificar un material hay que **darlo de alta como producto vendible** con SKU, precio y tarjeta de menú.
- `/logistica/materiales` no expone ninguna de las dos columnas (solo `MaterialClass`, que es otra cosa).
- El estado vacío del selector de recetas dice *"Los productos terminados se crean y clasifican desde Materiales"*
  y enlaza a `/logistica/materiales` ([`RestaurantRecipesPage.razor:122`](../src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor:122)) — **es un callejón sin salida**: ahí no se puede clasificar nada.
- El combo del POS ofrece `FinishedGood` / `SemiFinished` / `Resale`, pero **no** `RawMaterial`. Crear un
  producto vendible sobre un insumo lo reclasifica silenciosamente.
- Elegir "Reventa / snack" escribe `Resale`, valor que **ningún consumidor reconoce** → el material
  desaparece del selector de recetas.

Evidencia en datos: `salsa verde` tiene un producto vendible `BR-SALSAVERDE` a $10.00 en la tarjeta
"Salsa Verde Brunos". Fue el único camino disponible para marcarla como semielaborado.

---

## 2. Por qué salsa verde no aparece y totopos sí

Esta es la nota que pediste. La respuesta no es la que parece.

El filtro del selector es:

```csharp
// RestaurantRecipesPage.razor:301-303
private IReadOnlyList<RestaurantMaterialOption> ProductOptions => materialOptions.Where(option =>
  FindMaterial(option.Id) is { } material &&
  (string.Equals(material.ProductType, "FinishedGood", StringComparison.OrdinalIgnoreCase)
   || versions.Any(version => version.ProductMaterialId == material.Id)))
  .Select(option => option with { Group = "Productos terminados" }).ToList();
```

Se pasa por **una de dos** puertas: ser `FinishedGood`, **o ya tener al menos una versión de receta**.

| Material | `ProductType` | ¿`FinishedGood`? | ¿Ya tiene recetas? | ¿Aparece? |
|---|---|---|---|---|
| `salsa verde` (7205) | `SemiFinished` | ❌ | ❌ (0 headers, 0 versiones) | **No** |
| `TOTOPOS` (7252) | `RawMaterial` | ❌ | ✅ (4 versiones) | **Sí** |

**Totopos tampoco pasa el filtro de tipo** — es `RawMaterial`, igual de excluido que `SemiFinished`.
Aparece exclusivamente por la segunda puerta: ya tenía recetas.

Y la línea del tiempo explica por qué las tenía:

| Momento (UTC) | Evento |
|---|---|
| 2026-08-26 18:25:21 | Se crea `TOTOPOS` BOM v1 — **el filtro todavía no existía** |
| 2026-08-27 00:30:17 | Commit `726dd24` "Mejora del menu recetas." introduce `ProductOptions` |
| después | Totopos entra por la puerta de atrás; salsa verde queda fuera para siempre |

**Es un huevo-y-gallina:** para aparecer en el selector necesitas una receta, y para crear una receta
necesitas aparecer en el selector. Cualquier material que no sea `FinishedGood` y que no alcanzara a
tener receta antes del 27 de agosto quedó bloqueado de forma permanente. Salsa verde es el primer caso;
todo semielaborado nuevo caerá en lo mismo.

---

## 3. Defectos encontrados

### D1 · Bloqueante — El selector de recetas excluye semielaborados
`RestaurantRecipesPage.razor:301`. Descrito arriba. Impide crear la primera receta de cualquier
subproducto.

### D2 · Bloqueante — No existe pantalla para clasificar un material
El único escritor de `ProductType`/`FulfillmentMode` está dentro del alta de producto vendible del POS
(`RestaurantCatalogService.cs:346`). Obliga a inventar SKUs falsos. El texto de ayuda enlaza a una
pantalla que no ofrece la función.

### D3 · Bloqueante — La producción de subproductos está muerta
`/restaurante/produccion` exige `FulfillmentMode='MakeToStock'` **y** receta activa. En producción:

```
BOMs producibles ......... 0
Órdenes de producción .... 0   (histórico completo del RFC)
```

Los 8 subproductos reales con receta (`TOTOPOS`, `ADEREZO CHICKEN FINGER`, `CURTIDO DE PEPINOS`,
`EMPANIZADO CHIKEN FINGERS`, `ENSALADA DE COL`, `MARINADO DE SUERO DE LECHE`,
`MEZCLA DE ESPECIAS PARA SUERO DE LECHE`, `PETROLEO MICHELADA`) son `StockItem` → no producibles.
`salsa verde` sí es `MakeToStock` pero no tiene receta. **Los dos conjuntos no se intersectan.**

Impacto operativo vivo: `CURTIDO DE PEPINOS` tiene stock 0 y se usa en una receta activa. Ese producto
está bloqueado o pidiendo supervisor en el POS, y no hay forma de reponerlo por producción.

### D4 · Costo — `BaseUnitPrice` pisa silenciosamente el costo de la subreceta

```sql
-- BomRecipeService.cs:151 y :1128
COALESCE(material.BaseUnitPrice, subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity,0), 0)
```

El precio de compra gana. Los 12 subproductos tienen `BaseUnitPrice` capturado → **ninguna de sus recetas
influye en el costo del platillo padre**. La receta de totopos (7 tortillas + 50 ml aceite → 120 g) se
calcula, se congela… y se descarta a favor de un `0.06275`/g escrito a mano.

### D5 · Costo — `FrozenTheoreticalCost` nunca sube por el árbol
Se calcula una sola vez al activar ([`BomRecipeService.cs:639`](../src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs:639)). Reactivar la receta de salsa verde **no** recuesta chilaquiles.
No hay job de recosteo ni marca de "costo obsoleto".

### D6 · Integridad — Las guardas solo cubren subrecetas fantasma
`BomRecipeService.cs:778` filtra `childMaterial.FulfillmentMode = 'MakeToOrder'`. Archivar la receta de
`salsa verde` (que es `MakeToStock`) mientras chilaquiles y enchiladas suizas la consumen **se permite en
silencio**. Igual en `FindIncompleteSubassemblyAsync` (`:1097`, `:1104`).

### D7 · Higiene de datos — Precios de venta guardados en la columna de costo

| Material | `BaseUnitPrice` | Debería ser |
|---|---|---|
| `CHILAQUILES` | 80.00 | costo de receta |
| `HAMBURGUESA DE SIRLON BRUNOS` | 95.00 | costo de receta |
| `MOJITO DE MANGO` / `MANZANA` / `FRUTOS ROJOS` | 65.00 c/u | costo de receta |

Hoy son inertes porque esos materiales no se usan como componentes; el día que alguien los meta en un
combo, el costo del padre se dispara.

### D8 · Deriva de clasificación
Estado real en producción:

| `ProductType` | `FulfillmentMode` | Materiales | Con receta activa | Usados como ingrediente |
|---|---|---|---|---|
| `RawMaterial` | `StockItem` | 272 | **11** | 97 |
| `FinishedGood` | `MakeToOrder` | 33 | 29 | 2 |
| `FinishedGood` | `StockItem` | 18 | 0 | 2 |
| `SemiFinished` | `MakeToOrder` | 1 | 1 | 0 |
| `SemiFinished` | `MakeToStock` | 1 | 0 | 1 |

Las 18 filas `FinishedGood|StockItem` son reventa (refrescos, cervezas) mal tipadas.
`HAMBURGUESA DE SIRLON BRUNOS` está marcada `SemiFinished` pero es un producto terminado vendible que
nadie usa como ingrediente.

---

## 4. Modelo objetivo

### 4.1 Un solo concepto para el usuario: **Rol del material**

Se conserva el motor tal cual (todo sigue leyendo `FulfillmentMode`). Lo que cambia es que el usuario elige
**un** rol, y el rol escribe el par `(ProductType, FulfillmentMode)` de forma determinista y validada.

| Rol (etiqueta en UI) | `ProductType` | `FulfillmentMode` | Receta | Stock propio | Producible | Caso Bruno's |
|---|---|---|---|---|---|---|
| Insumo comprado | `RawMaterial` | `StockItem` | ✕ | ✓ | ✕ | tortillas, aceite |
| Artículo de reventa | `Resale` | `StockItem` | ✕ | ✓ | ✕ | Sprite, cerveza |
| **Subproducto por lote** | `SemiFinished` | `MakeToStock` | **obligatoria** | ✓ | **✓** | **salsa verde, totopos, aderezo** |
| **Subreceta al momento** | `SemiFinished` | `MakeToOrder` | **obligatoria** | ✕ | ✕ | papas gajo dentro de un combo |
| Producto terminado al momento | `FinishedGood` | `MakeToOrder` | obligatoria | ✕ | ✕ | chilaquiles |
| Producto terminado por lote | `FinishedGood` | `MakeToStock` | obligatoria | ✓ | ✓ | buñuelos, pan |

Los tres casos que describiste caen limpiamente:

- **salsa verde** → *Subproducto por lote*. Se produce en tanda, vive en inventario con su propia unidad
  (ml), chilaquiles consume 150 ml de ese stock. Su receta sirve para producir y para costear.
- **chilaquiles** → *Producto terminado al momento*. Tiene receta, no es subreceta de nadie.
- **papas gajo / chicken fingers dentro de otra receta** → *Subreceta al momento*. Sin stock propio; cada
  venta del padre se descuenta hasta materia prima.

### 4.2 Regla de costo

Se invierte la precedencia **solo para materiales producibles**:

- Con receta activa → costo = `FrozenTheoreticalCost / YieldQuantity`. `BaseUnitPrice` es el respaldo.
- Sin receta activa → costo = `BaseUnitPrice`, como hoy.
- Sin ninguno de los dos → 0 y `CostSource = "Sin costo configurado"` (ya existe el campo).

### 4.3 Propagación de costo

Al activar una receta, recostear hacia arriba todos los ancestros con versión activa, de abajo hacia
arriba, reusando la detección de ciclos que ya existe en `SaveDraftAsync` (`BomRecipeService.cs:415`).

---

## 5. Plan por fases

### Fase 0 — Desbloqueo (1 archivo, sin migración) · **hacer ya**

Objetivo: que salsa verde pueda tener receta hoy, sin esperar el resto.

1. **`RestaurantRecipesPage.razor:301`** — dejar de *filtrar* y pasar a *agrupar*. El selector muestra
   todos los materiales `Consumable` activos, ordenados en dos grupos:
   - "Productos y subproductos" — `FinishedGood`, `SemiFinished`, o cualquiera con versiones previas.
   - "Otros materiales" — el resto.
2. Al elegir un material del segundo grupo, mostrar un aviso en línea:
   *"Este material está clasificado como Insumo comprado. Su receta no se usará para producir ni para
   costear hasta que lo cambies a Subproducto por lote."* con acción directa (habilitada en Fase 1).
3. Corregir el texto y el enlace del estado vacío (`:122`), que hoy apuntan a una pantalla sin la función.

Criterio de aceptación: crear la receta de `salsa verde` desde la UI, sin SQL y sin inventar SKUs.

### Fase 1 — Clasificación editable y validada

4. `MaterialUpsertRequest` + `MaterialService` aceptan y persisten el **rol** (traducido al par de columnas).
5. Selector de rol en `/logistica/materiales` (detalle del material), con las 6 opciones de §4.1 y la
   explicación de cada una.
6. Acción rápida "Clasificar" desde el editor de recetas (resuelve el aviso de la Fase 0 sin cambiar de pantalla).
7. `SaveProductAsync` (`RestaurantCatalogService.cs:346`) deja de imponer clasificación: si el material ya
   tiene un rol, lo respeta; solo lo fija cuando está en el default sin tocar.
8. Migración: `CHECK CONSTRAINT` sobre los pares válidos + índice/vista de apoyo.
9. Advertencia al guardar un rol que contradiga los datos (p. ej. pasar a "Insumo comprado" un material con
   receta activa, o a "Subproducto por lote" uno sin receta).

### Fase 2 — Costeo correcto

10. Invertir el `COALESCE` para materiales con receta activa en `GetCostBreakdownAsync`
    (`BomRecipeService.cs:151`) y `CalculateTheoreticalCostAsync` (`:1128`).
11. Recosteo ascendente al activar una receta (§4.3).
12. Mostrar `CostSource` en `RestaurantRecipeCostBreakdown.razor` y marcar en ámbar los renglones donde un
    material producible se está costeando por `BaseUnitPrice`.
13. **Antes de aplicar 10**: reporte de impacto (costo actual vs. costo nuevo por receta activa). El cambio
    mueve el costo de todos los platillos con subproducto; no se despliega sin revisarlo.

### Fase 3 — Cerrar el ciclo de producción

14. Atajo "Planear producción" desde la ficha de receta cuando el rol sea producible.
15. En el reporte de alistamiento (`SuggestedProductAction`, `RestaurantSaleReadinessService.cs:731`):
    cuando el faltante sea un ingrediente `MakeToStock`, el texto debe decir *"planea una producción de X"*
    con enlace a `/restaurante/produccion`, no *"repón inventario"*.
16. En `/restaurante/produccion`, listar aparte los materiales **con receta activa pero no producibles**
    y ofrecer reclasificarlos. Hoy ese conjunto son 11 materiales y el usuario no tiene forma de verlo.

### Fase 4 — Guardas y diagnóstico

17. Extender la guarda de archivado (`:778`) y `FindIncompleteSubassemblyAsync` (`:1097`, `:1104`) para
    cubrir hijos `MakeToStock`: no bloquear la venta (el padre puede seguir vendiendo del stock existente),
    pero advertir que el subproducto quedará sin forma de reponerse.
18. Panel "¿Dónde se usa?" en la ficha de receta: recetas activas que consumen este material. El dato ya
    está en `BomComponent`; sin él no se puede evaluar el impacto de archivar.
19. Nuevas reglas de diagnóstico:
    - material con receta activa que no es producible;
    - material producible costeado por precio de compra;
    - `SemiFinished` sin receta;
    - `BaseUnitPrice` sospechoso de ser precio de venta (≫ suma de componentes).

---

## 6. Remediación de datos para `BRUNOS260707L26`

Ejecutar **después** de la Fase 1 (para que sea reversible desde la UI) y **antes** de la Fase 2
(para que el recosteo corra ya con la clasificación correcta).

**R1 — Reclasificar a "Subproducto por lote" (`SemiFinished` + `MakeToStock`):**
`TOTOPOS` (7252), `ADEREZO CHICKEN FINGER` (7242), `CURTIDO DE PEPINOS` (7198),
`EMPANIZADO CHIKEN FINGERS` (7195), `ENSALADA DE COL` (7197), `MARINADO DE SUERO DE LECHE` (7194),
`MEZCLA DE ESPECIAS PARA SUERO DE LECHE` (7196), `PETROLEO MICHELADA` (7248).

Sin efecto en el POS (`StockItem` y `MakeToStock` descuentan igual); los habilita para producción.

**R2 — Crear la receta de `salsa verde` (7205)**, ya desbloqueada por la Fase 0.

**R3 — `HAMBURGUESA DE SIRLON BRUNOS` (7066)** → decidir: hoy es `SemiFinished|MakeToOrder`, tiene receta
activa y **no** se usa como ingrediente de nadie. Parece "Producto terminado al momento".

**R4 — Reventa mal tipada:** las 18 filas `FinishedGood|StockItem` sin receta → `Resale|StockItem`.
Requiere que `Resale` sea un valor reconocido (Fase 1); hoy dejaría a esos materiales fuera del selector.

**R5 — Limpiar `BaseUnitPrice` con precios de venta** en `CHILAQUILES` (80), `HAMBURGUESA` (95) y los tres
mojitos (65). Tras la Fase 2 el costo saldrá de la receta.

**R6 — Decisión de negocio:** ¿el SKU `BR-SALSAVERDE` ($10.00, tarjeta "Salsa Verde Brunos") es una venta
real de salsa aparte, o solo fue el truco para poder clasificar el material? Si es lo segundo, se retira.

---

## 7. Pruebas

Cobertura existente relevante: `BomRecipeServiceTests.cs`, `RestaurantRecipesUxTests.cs`,
`RestaurantRecipeScalingTests.cs`.

Casos nuevos:

1. `ProductOptions` incluye un `SemiFinished` sin recetas (regresión directa de salsa verde).
2. `ProductOptions` incluye un `RawMaterial` sin recetas, en el grupo secundario y con aviso.
3. Guardar un rol produce exactamente el par `(ProductType, FulfillmentMode)` esperado; los pares inválidos
   se rechazan.
4. Costo: material con receta activa **y** `BaseUnitPrice` → gana la receta.
5. Costo: material con receta activa **sin** `BaseUnitPrice` → gana la receta (hoy ya pasa).
6. Costo: material sin receta **con** `BaseUnitPrice` → gana el precio.
7. Recosteo ascendente: activar la receta hija actualiza `FrozenTheoreticalCost` del padre.
8. Recosteo ascendente con ciclo → error controlado, sin recursión infinita.
9. Archivar la receta de un `MakeToStock` consumido por un padre activo → advertencia, no bloqueo.
10. Alistamiento: faltante de ingrediente `MakeToStock` sugiere producción, no reposición.
11. Un `MakeToStock` con receta activa aparece en el catálogo de `/restaurante/produccion`.

---

## Apéndice — Consultas de verificación

```sql
-- Materiales con receta activa que NO son producibles (deuda de clasificación)
SELECT m.Id, m.[Description], m.ProductType, m.FulfillmentMode, m.BaseUnitPrice
FROM logistica.Material m
WHERE m.Rfc = @Rfc
  AND m.FulfillmentMode <> 'MakeToStock'
  AND EXISTS (SELECT 1 FROM logistica.BomHeader h
              JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id AND v.[Status] = 'Active'
              WHERE h.Rfc = m.Rfc AND h.ProductMaterialId = m.Id)
ORDER BY m.[Description];

-- Subproductos cuyo costo se está tomando del precio de compra en vez de la receta
SELECT m.Id, m.[Description], m.BaseUnitPrice,
       v.FrozenTheoreticalCost / NULLIF(v.YieldQuantity, 0) AS CostoReceta
FROM logistica.Material m
JOIN logistica.BomHeader h ON h.Rfc = m.Rfc AND h.ProductMaterialId = m.Id
JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id AND v.[Status] = 'Active'
WHERE m.Rfc = @Rfc AND m.BaseUnitPrice IS NOT NULL;

-- BOMs elegibles para orden de producción (hoy: 0)
SELECT COUNT(*) FROM logistica.BomVersion v
JOIN logistica.BomHeader h ON h.Rfc = v.Rfc AND h.Id = v.BomHeaderId
JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
WHERE v.Rfc = @Rfc AND v.[Status] = 'Active' AND m.FulfillmentMode = 'MakeToStock' AND m.IsActive = 1;
```

---

# Ejecución — 2026-08-30

Todas las fases quedaron implementadas. Migraciones probadas en `Orion_SandBox`; **producción sin tocar**.

## Decisiones tomadas

| # | Decisión | Origen |
|---|---|---|
| D-1 | `HAMBURGUESA DE SIRLON BRUNOS` es producto terminado, no semielaborado | usuario (error humano) |
| D-2 | Salsa verde no se vende; el SKU `BR-SALSAVERDE` fue un intento de troubleshooting | usuario |
| D-3 | Aplicar la inversión del costeo + reporte de impacto | usuario |
| D-4 | No limpiar `BaseUnitPrice`; sólo diagnosticarlo | usuario |
| D-5 | El costo de receta sólo gana si el material **se produce** (`MakeToStock`/`MakeToOrder`); si está clasificado como comprado, manda el precio de compra | propia — hace que el rol sea lo que decide, y mantiene honesto el aviso de la Fase 0 |
| D-6 | Un `MakeToStock` sin receta **advierte**, no bloquea | propia — bloquear impediría activar chilaquiles mientras salsa verde no tenga receta |
| D-7 | La lista de "no producibles" sólo muestra `StockItem` con receta | propia — un `MakeToOrder` con receta es lo normal en un platillo; listar 33 platillos era ruido |

## Qué se hizo

**Fase 0** — El selector agrupa en vez de filtrar (`RestaurantRecipeProductRules`); aviso en línea con acción
"Clasificar como subproducto por lote"; corregido el estado vacío que enlazaba a una pantalla sin la función.

**Fase 1** — Rol de producción como concepto único (`MaterialProductionRoles`, 6 roles) editable en
`/logistica/materiales` y en el alta de producto del POS, donde sustituye a los dos dropdowns Tipo+Modo.
`SetProductionRoleAsync` para el cambio rápido. Migración `20260830_material_production_role.sql`:
normaliza por evidencia y fija `CK_Material_ProductionRole`.

**Fase 2** — Invertida la precedencia del costo en las dos consultas; recosteo ascendente al activar
(`RecostAncestorsAsync`); bandera `RecipeCostIgnored` en el desglose; script de recosteo
`20260830_recipe_cost_recalculation.sql` con reporte antes/después.

**Fase 3** — Atajo "Planear una producción" en la ficha de receta; el alistamiento sugiere *planear
producción* en vez de *reponer inventario* para faltantes `MakeToStock`; la página de producción explica
por qué el catálogo está vacío y lista lo que está atorado.

**Fase 4** — La guarda de archivado distingue subreceta al momento (bloquea) de subproducto por lote
(advierte); nueva guarda de unidad de rendimiento; panel "Dónde se usa"; advertencia de subproducto por
lote sin receta al activar.

## Resultado en `Orion_SandBox`

| Antes | Después |
|---|---|
| salsa verde no aparecía en el selector | aparece en "Productos y subproductos" |
| 0 BOMs producibles | **8** |
| 11 materiales con receta mal clasificados | 0 |
| 0 subproductos costeados por receta | 25 recetas recosteadas en cascada de 4 niveles |

32 materiales reclasificados (C1: 8 · C2: 2 · C3: 4 · C4: 1 · C5: 17). Migración idempotente.

## Bloqueante para producción · 3 rendimientos mal capturados

El recosteo destapó que tres recetas declaran el rendimiento en una unidad distinta a la unidad base de
su material. Eso carga el costo del lote completo sobre una sola unidad base y se propaga hacia arriba:
`CHICKEN FINGER BURGER` salió en **$3,939** y `CHICKEN FINGERS` en **$3,567**.

| Material | Rendimiento declarado | Unidad base | Qué falta |
|---|---|---|---|
| `EMPANIZADO CHIKEN FINGERS` (7195) | 1 KILOGRAMO | GRAMO | ¿cuántos gramos rinde la tanda? |
| `MEZCLA DE ESPECIAS PARA SUERO DE LECHE` (7196) | 1 GRAMO | MILILITRO | consume 40 g de especias |
| `CURTIDO DE PEPINOS` (7198) | 1 LITRO | REBANADA | ¿cuántas rebanadas rinde un litro? |

No los corregí: el rendimiento real es conocimiento de cocina, no se puede deducir de los datos.
El script de recosteo **se niega a correr** mientras exista una de estas recetas (`THROW 50002`), y la
aplicación ya no deja activar una receta así.

## Orden de aplicación en producción

```
1. Corregir los 3 rendimientos de arriba desde /restaurante/recetas
2. 20260830_material_production_role.sql
3. 20260830_recipe_cost_recalculation.sql   (aborta solo si falta el paso 1)
4. Revisar logistica.BomCostRecalculationLog antes de dar por buenos los costos
```

`logistica.MaterialProductionRoleBackfill` guarda el antes/después de cada reclasificación.

## Pendiente

- **R2** — la receta de salsa verde: ya se puede capturar en la UI, pero las cantidades son de cocina.
- **R6** — retirar el SKU `BR-SALSAVERDE` (D-2). No lo hice: es un producto vendible en producción y
  preferí no desactivar algo del POS sin que lo veas.
- Regla de diagnóstico de `BaseUnitPrice` sospechoso de ser precio de venta. Las otras tres reglas
  previstas quedaron cubiertas en lugares más útiles (página de producción, desglose de costo,
  validación de activación).

---

# Aplicación a producción — pendiente de ejecutar

La escritura a `grupocarpio` quedó **bloqueada por el clasificador de permisos** de la sesión.
Los scripts están probados en `Orion_SandBox` y listos; hay que correrlos manualmente.

## Producción no es igual al sandbox

`Orion_SandBox` es un snapshot anterior. Diferencias que importan:

- En **producción**, `EMPANIZADO` (7195), `MEZCLA DE ESPECIAS` (7196) y `CURTIDO DE PEPINOS` (7198)
  **ya están** como `SemiFinished · MakeToStock`. Por eso la reclasificación toca **29** materiales
  en producción y no 32.
- El problema de rendimientos **sí existe igual** en producción: las mismas 3 recetas.

Reclasificación que aplicaría en producción:

| Regla | Cambio | n |
|---|---|---|
| C1 | `RawMaterial·StockItem` → `SemiFinished·MakeToStock` | 5 |
| C2 | `FinishedGood·MakeToOrder` → `SemiFinished·MakeToOrder` | 2 |
| C3 | → `FinishedGood·MakeToOrder` | 4 |
| C4 | `FinishedGood·StockItem` → `FinishedGood·MakeToStock` | 1 |
| C5 | `FinishedGood·StockItem` → `Resale·StockItem` | 17 |

C1: ADEREZO CHICKEN FINGER · ENSALADA DE COL · MARINADO DE SUERO DE LECHE · PETROLEO MICHELADA · TOTOPOS
C2: CHICKEN FINGERS · PAPAS Gajo — C3: HAMBURGUESA DE SIRLON · los 3 mojitos — C4: BINUELO

## Comandos

```
sqlcmd -S 127.0.0.1,1433 -U orion -P <pwd> -C -d grupocarpio -b -i src/OrionERP.Infrastructure/Features/Logistica/Sql/20260830_material_production_role.sql
sqlcmd -S 127.0.0.1,1433 -U orion -P <pwd> -C -d grupocarpio -b -i src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260830_fix_recipe_yield_units.sql
sqlcmd -S 127.0.0.1,1433 -U orion -P <pwd> -C -d grupocarpio -b -i src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260830_recipe_cost_recalculation.sql
```

El tercero **abortará** hasta que se corrijan a mano los dos rendimientos que no se pueden derivar.

## Reversa

```sql
-- deshacer la reclasificación
UPDATE m SET m.ProductType = b.OldProductType, m.FulfillmentMode = b.OldFulfillmentMode
FROM logistica.Material m
JOIN logistica.MaterialProductionRoleBackfill b ON b.Rfc = m.Rfc AND b.MaterialId = m.Id;
ALTER TABLE logistica.Material DROP CONSTRAINT CK_Material_ProductionRole;

-- deshacer la conversión de rendimiento
UPDATE v SET v.YieldQuantity = l.OldYieldQuantity, v.YieldUnitId = l.OldYieldUnitId
FROM logistica.BomVersion v
JOIN logistica.BomYieldUnitFixLog l ON l.Rfc = v.Rfc AND l.BomVersionId = v.Id;

-- deshacer el recosteo
UPDATE v SET v.FrozenTheoreticalCost = l.OldUnitCost
FROM logistica.BomVersion v
JOIN logistica.BomCostRecalculationLog l ON l.Rfc = v.Rfc AND l.BomVersionId = v.BomVersionId;
```

## Los dos datos que faltan

| Material | Dice que rinde | Se inventaría en | Se consume como | Pregunta |
|---|---|---|---|---|
| `MEZCLA DE ESPECIAS PARA SUERO DE LECHE` (7196) | 1 GRAMO | MILILITRO | 40 ml por orden de chicken fingers | La tanda son 40 g de especias. ¿El rendimiento son 40, y la unidad base debería ser GRAMO en vez de MILILITRO? |
| `CURTIDO DE PEPINOS` (7198) | 1 LITRO | REBANADA | 4 rebanadas por ensalada de col | ¿Cuántas rebanadas salen de la tanda (2 pepinos)? |

---

# Cierre — orden final de ejecución

Tras las correcciones hechas a mano en la UI (rendimientos, unidades base, receta de salsa verde,
receta v16 de chicken fingers), quedaron cuatro pendientes que se resuelven por SQL en
`20260830_bruno_recipe_data_fixes.sql`:

| | Corrección | Verificado contra producción |
|---|---|---|
| F1 | `CHICKEN FINGER BURGER` consume chicken fingers en ORDEN, unidad base PIEZA → **2 PIEZA** | componente 1480 |
| F2 | `EMPANIZADO` rinde 100 g → **700 g**, precio $0.30 → **$0.043272**/g | versión 299, lote $30.29 |
| F3 | `HAMBURGUESA DE SIRLON` (7066) → `FinishedGood · MakeToOrder` | |
| F4 | `CHICKEN FINGERS` (6928) → `SemiFinished · MakeToOrder` | |

F1 y F2 editan versiones activas en sitio, cosa que la aplicación no permite. Se hace por SQL
porque son correcciones de datos defectuosos, no evolución de receta. Queda registro en
`logistica.BomRecipeDataFixLog`.

## Orden

```
1. 20260830_bruno_recipe_data_fixes.sql      -- F1..F4, termina con una verificación
2. 20260830_material_production_role.sql     -- 22 materiales + CK_Material_ProductionRole
3. 20260830_recipe_cost_recalculation.sql    -- recosteo en cascada
```

`20260830_fix_recipe_yield_units.sql` ya no aplica: cero rendimientos fuera de unidad base.

## Proyección tras aplicar los tres

Ningún margen negativo. El más bajo es `ORDEN DE PAPAS DE GAJO` con 34%; el resto, 40% o más.

| | Costo hoy | Costo después | Venta | Margen |
|---|---|---|---|---|
| CHICKEN FINGER BURGER | $16.21 | $37.52 | $85.00 | 56% |
| CHICKEN FINGERS | $8.21 | $9.69 | — | subreceta |
| EMPANIZADO (por gramo) | $0.30 | $0.04 | — | subproducto |
| NACHOS | $22.43 | $30.27 | $65.00 | 53% |

Las bebidas —margarita, cantarito, mojitos, micheladas— pasan de costo cero a costo real.

## Hueco conocido que queda abierto

Cambiar la unidad base de un material deja rotas las recetas activas que lo consumen en la
unidad anterior: el motor las marca `BOM_CONVERSION_MISSING`, el ingrediente aporta $0 al costo
y el platillo queda bloqueado por configuración. Pasó dos veces durante esta corrección
(`MEZCLA DE ESPECIAS` y `CHICKEN FINGERS`). La validación existente sólo corre al activar una
receta, no al editar el material. Falta una guarda en el guardado de material.
