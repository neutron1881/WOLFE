# GexBotA Classic — Informe Técnico de Auditoría

**Producto:** GexBotA Classic  
**Versión:** Full-Beta-1.0  
**Fecha:** Julio 2025  
**Plataforma objetivo:** ATAS (OrderFlow Trading)  
**Repositorio:** `https://github.com/neutron1881/WOLFE` (rama `Full-Beta-1.0`)  
**Autor del informe:** Equipo de desarrollo GexBotA  

---

## 1. Resumen Ejecutivo

GexBotA Classic es un indicador profesional para la plataforma de trading ATAS que visualiza la **Gamma Exposure (GEX)** en tiempo real, consumiendo datos de la API REST de Gexbot. El indicador superpone un perfil de gamma sobre el gráfico de precios y proporciona un sistema de **4 alertas algorítmicas** que detectan condiciones estructurales del mercado basadas en la distribución de gamma de los creadores de mercado.

El producto transforma datos crudos de opciones en inteligencia accionable para traders de futuros y acciones, eliminando la necesidad de interpretar manualmente tablas de gamma.

---

## 2. Stack Tecnológico

| Componente | Tecnología | Justificación |
|---|---|---|
| **Runtime** | .NET 8.0 (LTS) | Última versión de soporte a largo plazo. Rendimiento superior en hot paths, GC mejorado, y soporte oficial hasta noviembre 2026 |
| **Target Framework** | `net8.0-windows` | Requerido por ATAS (plataforma WPF nativa de Windows) |
| **Lenguaje** | C# 12 | Features como `file-scoped namespaces`, `collection expressions`, `primary constructors`, pattern matching avanzado |
| **SDK Style** | `Microsoft.NET.Sdk` | Proyecto moderno SDK-style, limpio y mantenible vs legacy `.csproj` |
| **UI Framework** | WPF (via `UseWPF=true`) | Obligatorio para la integración con el motor de rendering de ATAS |
| **Rendering** | `OFT.Rendering` (ATAS SDK) | API nativa de ATAS para dibujo en canvas: `RenderContext`, `RenderPen`, `RenderFont` |
| **HTTP Client** | `System.Net.Http.HttpClient` | Cliente HTTP nativo del framework. Sin dependencias externas. Connection pooling automático |
| **JSON Parsing** | `System.Text.Json` | Parser de alto rendimiento incluido en .NET. Zero-dependency, zero-copy streaming via `JsonDocument.ParseAsync` |
| **Drawing** | `System.Drawing.Common 8.0` | Tipos primitivos (`Color`, `Rectangle`, `DashStyle`) requeridos por la API de rendering de ATAS |
| **LINQ extensions** | `MoreLinq 4.4.0` | Única dependencia NuGet. Proporciona operadores LINQ extendidos para consultas complejas sobre colecciones de strikes |
| **Deploy** | PostBuild copy | El `.dll` compilado se copia automáticamente a `%APPDATA%\ATAS\Indicators` en cada build |

### ¿Por qué no se usaron otras tecnologías?

| Alternativa descartada | Razón |
|---|---|
| **Newtonsoft.Json** | `System.Text.Json` es más rápido, no requiere dependencia externa, y soporta streaming nativo (menos allocations) |
| **HttpWebRequest** | API obsoleta. `HttpClient` es el estándar moderno con connection pooling y async nativo |
| **BackgroundWorker** | Patrón legacy. Se usa `Task.Run` + `async/await` + `ManualResetEventSlim` para control preciso del loop de fondo |
| **System.Timers.Timer** | Reemplazado por un loop async con `Task.Delay` para evitar reentrancia y permitir señalización inmediata |
| **SignalR / WebSocket** | La API de Gexbot es REST pura; no expone endpoint de streaming. El polling async con timestamp-based change detection es la solución óptima |

---

## 3. Arquitectura del Proyecto

### 3.1 Estructura de Archivos

```
Indicadores/
├── GexBotA.csproj                      # Proyecto SDK-style, .NET 8.0-windows
├── GexBotAClassic.cs                   # Indicador principal (2,154 líneas)
├── Models/
│   └── GexClassicData.cs               # Modelos de datos (71 líneas)
├── Services/
│   └── GexApiClient.cs                 # Cliente HTTP async (247 líneas)
└── alertas.txt                         # Especificación funcional de alertas
```

**Total:** ~2,472 líneas de código productivo en 3 archivos.

### 3.2 Decisión Arquitectónica: Archivo Único para el Indicador

El indicador (`GexBotAClassic.cs`) se mantiene como una sola clase `sealed` de ~2,100 líneas. Esta decisión es **deliberada**:

- **Restricción de ATAS:** La plataforma espera una clase que herede de `Indicator` como unidad atómica. Fragmentar en `partial class` o archivos separados introduce complejidad sin beneficio real en este contexto.
- **Cohesión funcional:** Todos los métodos operan sobre el mismo estado (`_gexData`, `_dataLock`, recursos de rendering). Separarlos requeriría pasar estado o crear abstracciones innecesarias.
- **Organización interna:** Se compensa con un sistema riguroso de `#region` (27 regiones) que permite navegación IDE eficiente.

### 3.3 Mapa de Regiones (GexBotAClassic.cs)

```
┌─ Enums                          (L18-108)    Tipos de configuración
├─ Constants                       (L110-114)   Labels estáticas
├─ Private Fields                  (L116-277)   Todo el estado mutable
│
├─ Properties - Setup              (L279-337)   Ticker, API key, refresh
├─ Properties - Conversion         (L338-357)   Factor de conversión de precios
├─ Properties - General Options    (L358-441)   Panel, colores, barras, center line
├─ Properties - Labels             (L442-568)   Strike labels + Gamma Call/Put labels
├─ Properties - Priors Dots        (L569-668)   4 temporalidades independientes
├─ Properties - Scaling Options    (L669-679)   Escalado logarítmico
├─ Properties - Majors             (L680-726)   Líneas Major+/Major−
├─ Properties - Misc               (L727-773)   Zero Gamma, Spot line
├─ Properties - Alert 1            (L774-820)   Agotamiento Alcista
├─ Properties - Alert 2            (L821-850)   Efecto Imán
├─ Properties - Alert 3            (L851-889)   Liquidación Institucional
├─ Properties - Alert 4            (L890-937)   Muro Gamma Negativo
├─ Properties - Info Panel         (L938-977)   Panel informativo horizontal
│
├─ Constructor                     (L978-993)   Inicialización, pens, fonts
├─ Indicator Lifecycle             (L994-1030)  OnCalculate, OnInitialize, OnDispose
│
├─ Rendering - Main                (L1031-1098) OnRender dispatcher
├─ Rendering - Histogram           (L1099-1145) Barras de GEX + center line
├─ Rendering - Lookback Dots       (L1146-1201) Prior dots per-temporal
├─ Rendering - Strike Labels       (L1202-1275) Labels de precio + gamma
├─ Rendering - Key Levels          (L1276-1320) Líneas horizontales (Zero, Majors, Spot)
├─ Rendering - Info Panel          (L1321-1412) Panel de datos en 4 columnas
├─ Rendering - Alert Overlay       (L1413-1468) Overlay flotante con fade
├─ Rendering - Status              (L1469-1493) Mensajes de carga/error
│
├─ Data Fetching                   (L1494-1580) Loop async + signal + fetch
├─ Alerts                          (L1581-2019) ProcessAlerts + 4 algoritmos
│
├─ Helpers - Coordinates           (L2020-2101) PriceToY, ApplyMultiplier, Scale
├─ Helpers - Ticker Config         (L2102-2115) Mapeo de tickers a API
├─ Helpers - Resource Management   (L2116-2139) RebuildPens, RebuildFonts
└─ Helpers - Formatting            (L2140-2154) FormatGexValue (K/MM/B)
```

---

## 4. Arquitectura de Datos

### 4.1 Modelos (`Models/GexClassicData.cs`)

```
GexClassicData (respuesta completa /classic/)
├── Timestamp, Ticker, Spot, ZeroGamma
├── MajorPosVol/Oi, MajorNegVol/Oi
├── SumGexVol/Oi
├── Strikes: List<StrikeData>
│   └── Strike, GexByVolume, GexByOi, Priors[5]
└── MaxPriors: List<MaxChangeEntry>
    └── Strike, GexChange

GexMajorsData (respuesta ligera /majors/)
├── Timestamp, Ticker, Spot
├── MajorPosVol/Oi, MajorNegVol/Oi
├── ZeroGamma, NetGexVol/Oi
```

### 4.2 Flujo de Datos

```
API Gexbot (REST/JSON)
    │
    ▼
GexApiClient.FetchClassicAsync()          ← HttpClient + stream JSON
    │  SemaphoreSlim(1,1) single-flight
    │  ReadAsStreamAsync → JsonDocument.ParseAsync (zero-copy)
    ▼
GexClassicData (parsed, inmutable por fetch)
    │
    ▼
BackgroundFetchLoopAsync()                ← Task.Run, loop infinito
    │  ManualResetEventSlim para señalización
    │  Timestamp-based change detection
    │  lock(_dataLock) para escritura thread-safe
    ▼
_gexData (shared state)
    │
    ├──▶ OnRender() → lee con lock → dibuja histograma, labels, levels
    ├──▶ ProcessAlerts() → evalúa 4 algoritmos de alerta
    └──▶ RecalcAutoFactor() → ajusta factor de conversión
```

---

## 5. Arquitectura de Comunicación Async

### 5.1 ¿Por qué Async?

Las llamadas HTTP a Gexbot son operaciones de I/O externas con latencia variable (100–800 ms). En ATAS:

- `OnCalculate` se ejecuta en cada tick/barra nueva (potencialmente cientos de veces por segundo).
- `OnRender` se ejecuta en cada redraw del canvas.
- Ambos corren en el thread principal/UI.

Una llamada HTTP síncrona bloquearía el gráfico durante la respuesta → freezes, velas que no se actualizan, clics que no responden.

### 5.2 Patrón Implementado: Background Async Loop con Señalización

```csharp
// En lugar de Timer (reentrante) o Task.Run por-fetch (leak de tasks):
private async Task BackgroundFetchLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        _fetchSignal.Wait(ct);        // Duerme hasta señal o timeout
        _fetchSignal.Reset();
        
        var data = await _apiClient.FetchClassicAsync(..., ct);
        
        if (data != null && data.Timestamp != _lastDataTimestamp)
        {
            lock (_dataLock) { _gexData = data; }
            RedrawChart(...);
        }
        
        _fetchSignal.Wait(intervalMs, ct);  // Delay configurable
    }
}
```

**Ventajas sobre alternativas:**

| Aspecto | Timer + Task.Run | Loop Async (implementado) |
|---|---|---|
| Tasks creados | 1 nuevo por cada tick del timer | 1 único Task de larga duración |
| Reentrancia | Posible si el timer dispara antes de que acabe el fetch | Imposible: el loop es secuencial |
| Re-trigger inmediato | Hay que destruir y recrear el timer | `_fetchSignal.Set()` — O(1), instantáneo |
| Cancelación limpia | Timer.Dispose + CTS.Cancel (race conditions) | CTS.Cancel + signal.Set + await loop |
| Overhead de memoria | Allocations por Task + closure por cada disparo | Zero allocations en steady state |

### 5.3 Cliente HTTP (`GexApiClient`)

```
SemaphoreSlim(1,1) ──── Garantiza single-flight (no requests duplicados)
    │
    ▼
HttpClient.GetAsync(HttpCompletionOption.ResponseHeadersRead)
    │  ← Empieza a leer sin esperar el body completo
    ▼
response.Content.ReadAsStreamAsync()
    │  ← Stream directo, sin string intermedia
    ▼
JsonDocument.ParseAsync(stream)
    │  ← Zero-copy parsing desde el stream
    ▼
ParseClassicResponse(root)
    │  ← Iteración sin List<JsonElement> intermedias
    ▼
GexClassicData (resultado)
```

---

## 6. Sistema de Rendering

### 6.1 Pipeline de Dibujo

El rendering se ejecuta en `OnRender` (thread de UI de ATAS) y sigue un orden de capas estricto:

```
1. Background panel          ← Rectángulo semi-transparente
2. Center line (divider)     ← Línea vertical Call/Put
3. Histogram bars            ← Barras horizontales bidireccionales
4. Lookback dots             ← Prior dots por temporalidad (1m, 5m, 15m, 30m)
5. Strike/Gamma labels       ← Texto en el tip de cada barra
6. Key levels                ← Líneas horizontales (Zero Γ, Major+, Major−, Spot)
7. Info panel                ← Panel de datos en 4 columnas (top)
8. Alert overlay             ← Mensaje flotante con fade (si activo)
```

### 6.2 Recursos de Rendering Pre-construidos

Los objetos `RenderPen` y `RenderFont` se **pre-alocan** en `RebuildPens()` / `RebuildFonts()` y se reutilizan en cada frame. No se crean objetos de rendering dentro del loop de `OnRender`.

```csharp
// Pre-built (constructor + property changes):
_zeroGammaPen = new RenderPen(_zeroLineColor, _zeroLineWidth, _zeroLineStyle);
_majorPosPen  = new RenderPen(_majorPosColor, _majorLineWidth, _majorLineStyle);
// ... 5 pens + 7 fonts pre-alocados

// En OnRender: solo se usan, nunca se crean
context.DrawLine(_zeroGammaPen, x1, y, x2, y);
```

### 6.3 Conversión de Precios

El indicador soporta 3 modos de conversión para mapear precios de la API (ej. QQQ a $520) a precios del chart (ej. NQ a $21,000):

| Modo | Comportamiento |
|---|---|
| `None` | Factor = 1.0 (API y chart usan el mismo precio) |
| `Auto` | Factor = chartPrice / apiSpot (calculado dinámicamente) |
| `Manual` | Factor definido por el usuario (ej. 40.0 para QQQ→NQ) |

Los **strike labels** siempre muestran el precio original del ticker (API), no el convertido, para que el usuario identifique los strikes reales del mercado de opciones.

---

## 7. Sistema de Alertas Algorítmicas

### 7.1 Arquitectura General

```
ProcessAlerts(data)                       ← Llamado desde OnRender
    │
    ├── Spot touching Major+ / Major−     ← Alertas simples de proximidad
    │
    ├── Alert 1: CheckBullishExhaustionAlert()
    ├── Alert 2: CheckMagnetAlert()
    ├── Alert 3: CheckLiquidationAlert()
    └── Alert 4: CheckNegativeWallAlert()
          │
          ├── AddAlert("alert", msg)      ← Sistema de alertas nativo ATAS
          └── ShowAlertOverlay(msg, color) ← Overlay visual con fade de 30s
```

### 7.2 Las 4 Alertas

#### Alert 1: Agotamiento Alcista (Bullish Exhaustion)

- **Detecta:** Precio supera Major+ pero no hay gamma arriba → rally sin soporte.
- **Algoritmo:** Spot > Major+ → suma GEX de N strikes superiores → si < 15% del Major+ GEX → vacío.
- **Utilidad:** Evitar compras tardías, buscar reversiones (Fade).
- **Color overlay:** 🔴 Rojo.

#### Alert 2: Efecto Imán (Magnet / Vía Libre)

- **Detecta:** Precio cruza Zero Gamma al alza + bloque masivo de Call Gamma concentrado arriba.
- **Algoritmo:** Edge detection (below→above ZG) → strike con max GEX+ → si > 40% del total arriba → imán.
- **Utilidad:** Confianza para mantener posiciones largas (Hold + trailing stop).
- **Color overlay:** 🟢 Verde.

#### Alert 3: Liquidación Institucional (Gamma Drop)

- **Detecta:** Caída súbita de gamma en Major+ mientras el precio está cerca → institucionales cerrando opciones.
- **Algoritmo:** Spot cerca de Major+ → compara GEX actual vs `Priors[3]` (15 min ago) → si drop > 30% → liquidación.
- **Utilidad:** Aviso de que el muro se desmorona, reversión inminente.
- **Color overlay:** 🟡 Naranja/Gold.

#### Alert 4: Muro Gamma Negativo (Hard Support)

- **Detecta:** Precio cae hacia Major− y no hay gamma debajo → última barrera de MM.
- **Algoritmo:** Spot encima de Major− (dentro de N ticks) → suma |GEX| de N strikes debajo → si < 10% de |Major− GEX| → vacío total.
- **Utilidad:** Zona de rebote extremo, stop ajustado por debajo.
- **Color overlay:** 🔵 Azul.

### 7.3 Sistema Anti-Spam

Cada alerta implementa un **cooldown independiente** con `DateTime`:

| Alerta | Cooldown por defecto | Mecanismo adicional |
|---|---|---|
| Alert 1 | 5 min | — |
| Alert 2 | 10 min | Re-arm si precio cae bajo Zero Gamma |
| Alert 3 | 15 min | Alerta estructural, espacio largo |
| Alert 4 | 5 min | Rebotes rápidos necesitan reactivación ágil |

### 7.4 Overlay Visual

Las alertas generan un cuadro flotante en la esquina superior derecha con:
- Borde superior de color (accent line) según tipo de alerta.
- Fade automático: opacidad completa 20 segundos → fade-out lineal 10 segundos → desaparece.
- Se posiciona debajo del info panel si está activo.

---

## 8. Configurabilidad

El indicador expone **60+ propiedades** configurables agrupadas en 13 secciones:

```
Setup                    → Ticker, aggregation, API key, refresh interval
Conversion               → None / Auto / Manual + factor
General Options          → Panel location/size, colors, bar width, center line
Labels                   → Strike labels + Gamma Call/Put labels (independientes)
Priors Dots              → 4 temporalidades (1m, 5m, 15m, 30m) con toggle/color/size
Scaling Options          → Logarítmico
Majors                   → Show/hide, colors, line style/width
Misc                     → Zero Gamma line, Spot line
Alert 1: Agotamiento     → Toggle, scan strikes, threshold%, cooldown
Alert 2: Efecto Imán     → Toggle, concentration%, cooldown
Alert 3: Liquidaciones   → Toggle, drop%, proximity, cooldown
Alert 4: Muro Negativo   → Toggle, scan strikes, vacuum%, proximity, cooldown
Info Panel               → Show/hide, height, colors
```

Todas las propiedades usan atributos `[Display]` con `Name`, `GroupName`, `Order` y `Description` para renderizado automático en el panel de settings de ATAS. Se aplican `[Range]` y `Math.Clamp` para validación de rangos.

---

## 9. Thread Safety

| Recurso compartido | Mecanismo de protección |
|---|---|
| `_gexData` (datos de API) | `lock(_dataLock)` en lectura (OnRender) y escritura (fetch loop) |
| HTTP requests en vuelo | `SemaphoreSlim(1,1)` en `GexApiClient` — single-flight |
| Señalización de fetch | `ManualResetEventSlim` — thread-safe por diseño |
| Timestamps de alertas | Escritura solo desde OnRender (thread UI) — no necesita lock |
| Recursos de rendering | Inmutables después de `Rebuild*()` — no necesitan lock |

---

## 10. Instrumentos Soportados

El indicador soporta **40+ tickers** organizados en 3 categorías:

- **Índices (6):** SPX, NDX, RUT, VIX + variantes con conversión (SPX→ES, NDX→NQ)
- **ETFs (8):** SPY, QQQ, IWM, TLT, GLD, USO, TQQQ, UVXY
- **Acciones (26):** AAPL, AMD, AMZN, AVGO, BABA, COIN, GME, GOOG, META, MSFT, NVDA, PLTR, TSLA, etc.

---

## 11. Puntos Fuertes

### Arquitectura
- ✅ **Async puro end-to-end**: Ninguna operación de I/O bloquea el thread de UI. El gráfico permanece fluido incluso con polling cada segundo.
- ✅ **Zero-copy JSON streaming**: `ReadAsStreamAsync` → `JsonDocument.ParseAsync` elimina la string intermedia del JSON completo, reduciendo allocations y presión de GC.
- ✅ **Timestamp-based change detection**: Solo se redibuja el chart cuando los datos realmente cambian, evitando renders innecesarios.
- ✅ **Señalización instantánea**: Cambios de configuración (ticker, aggregation) despiertan el loop inmediatamente via `ManualResetEventSlim`, sin esperar al siguiente intervalo.

### Rendering
- ✅ **Recursos pre-alocados**: Pens y fonts se crean una vez y se reutilizan. Zero allocations en el hot path de OnRender.
- ✅ **Layered rendering**: 8 capas ordenadas (background → bars → dots → labels → levels → panel → overlay) con clipping correcto.
- ✅ **Prior dots per-temporal**: 4 temporalidades con configuración completamente independiente (color, tamaño, toggle).

### Alertas
- ✅ **4 algoritmos complementarios**: Cubren las 4 situaciones estructurales principales (exhaustion, magnet, liquidation, hard support).
- ✅ **Anti-spam con cooldown independiente**: Cada alerta tiene su propio timer, adaptado a la naturaleza de la señal.
- ✅ **Doble notificación**: Sistema nativo de ATAS (`AddAlert`) + overlay visual con fade automático.
- ✅ **Edge detection** en Alert 2: Solo dispara en la transición below→above, no en cada tick por encima.

### Experiencia de Usuario
- ✅ **60+ settings configurables** con validación de rangos.
- ✅ **Conversión automática de precios** (Auto mode) para operar futuros con datos de opciones del subyacente.
- ✅ **Strike labels muestran precio del ticker** (no del chart), manteniendo la referencia al mercado de opciones real.

---

## 12. Puntos a Mejorar (Roadmap Futuro)

### Prioridad Alta
| Mejora | Descripción | Impacto |
|---|---|---|
| **Unit Tests** | No hay cobertura de tests automatizados. Los algoritmos de alerta y el parsing JSON deberían tener tests unitarios | Calidad / Regresiones |
| **Logging estructurado** | No hay sistema de logging. Los errores de API se almacenan en `_lastError` pero no se persisten | Diagnóstico en producción |
| **Alertas bearish complementarias** | Alert 1 (Exhaustion) y Alert 2 (Magnet) solo cubren el lado alcista. Faltan las versiones simétricas para el lado bajista | Cobertura funcional |

### Prioridad Media
| Mejora | Descripción | Impacto |
|---|---|---|
| **Partial class split** | Con 2,154 líneas, el archivo principal podría beneficiarse de `partial class` para separar Rendering, Alerts y Properties en archivos independientes | Mantenibilidad |
| **Caché en disco** | Si la API no responde, no hay fallback a datos previos persistidos. Un caché JSON en disco permitiría arrancar con datos stale | Resiliencia |
| **Rate limiting del cliente** | El `GexApiClient` no implementa rate limiting explícito. Si la API de Gexbot tiene límites, podrían ocurrir `429 Too Many Requests` | Estabilidad |
| **Health check visual** | Mostrar un indicador de estado de conexión (verde/rojo) en el info panel | UX |

### Prioridad Baja
| Mejora | Descripción | Impacto |
|---|---|---|
| **Internacionalización** | Los mensajes de alerta están en español/inglés mezclados. Un sistema de recursos `.resx` permitiría localización completa | Mercado internacional |
| **Tooltips en Prior Dots** | Mostrar el valor exacto de gamma al hacer hover sobre un dot | UX avanzada |
| **Export de datos** | Permitir exportar el perfil de gamma actual a CSV/JSON | Análisis offline |
| **Multi-timeframe overlay** | Superponer perfiles de diferentes aggregations (Full + Latest) simultáneamente | Feature avanzada |

---

## 13. Dependencias y Licencias

| Dependencia | Versión | Tipo | Licencia |
|---|---|---|---|
| .NET 8.0 Runtime | 8.0.x | Framework | MIT |
| System.Drawing.Common | 8.0.0 | NuGet | MIT |
| MoreLinq | 4.4.0 | NuGet | Apache 2.0 |
| ATAS.Indicators | — | Local DLL | Propietaria (ATAS SDK) |
| ATAS.Indicators.Technical | — | Local DLL | Propietaria (ATAS SDK) |
| OFT.Rendering | — | Local DLL | Propietaria (ATAS SDK) |
| OFT.Rendering.Wpf | — | Local DLL | Propietaria (ATAS SDK) |
| OFT.Attributes | — | Local DLL | Propietaria (ATAS SDK) |
| OFT.Localization | — | Local DLL | Propietaria (ATAS SDK) |
| Utils.Common | — | Local DLL | Propietaria (ATAS SDK) |

**Nota:** Todas las dependencias NuGet (`System.Drawing.Common`, `MoreLinq`) tienen licencias permisivas (MIT / Apache 2.0) compatibles con distribución comercial. Las DLLs de ATAS son parte del SDK de la plataforma y se distribuyen con la instalación de ATAS.

---

## 14. Métricas de Código

| Métrica | Valor |
|---|---|
| Archivos de código productivo | 3 |
| Líneas totales (productivas) | ~2,472 |
| `GexBotAClassic.cs` | 2,154 líneas |
| `GexApiClient.cs` | 247 líneas |
| `GexClassicData.cs` | 71 líneas |
| Regiones (`#region`) | 27 |
| Propiedades configurables | 60+ |
| Enums | 6 |
| Modelos de datos | 4 clases |
| Algoritmos de alerta | 4 |
| Tickers soportados | 40+ |
| Dependencias NuGet | 2 |

---

## 15. Conclusión

GexBotA Classic es un indicador técnicamente sólido que combina una arquitectura async bien diseñada con un sistema de rendering optimizado y alertas algorítmicas basadas en la estructura de gamma del mercado. El código es limpio, está organizado en regiones claras, y todas las decisiones arquitectónicas (async loop vs timer, stream JSON vs string, pre-allocated pens, per-temporal prior dots) están justificadas por requisitos de rendimiento y la restricción de no bloquear el thread de UI de ATAS.

Las áreas principales de mejora (unit tests, logging, alertas bearish) son extensiones naturales del producto y no representan deuda técnica crítica. El producto está listo para distribución comercial en su estado actual con la recomendación de añadir cobertura de tests antes del release GA.

---

*Documento generado como parte del proceso de auditoría técnica para aprobación de distribución comercial.*

---
---

# PARTE 2 — Anexos Técnicos Detallados

---

## A1. Contrato de la API de Gexbot

### A1.1 Endpoints Consumidos

| Endpoint | Método | Uso |
|---|---|---|
| `GET /{TICKER}/classic/{AGG}?key={KEY}` | `FetchClassicAsync` | Datos completos: histograma + niveles + priors |
| `GET /{TICKER}/classic/{AGG}/majors?key={KEY}` | `FetchMajorsAsync` | Solo niveles clave (respuesta ligera, polling rápido) |

### A1.2 Estructura del JSON `/classic/`

```json
{
  "timestamp": 1719408000,
  "ticker": "SPX",
  "min_dte": 0,
  "sec_min_dte": 2,
  "spot": 5320.45,
  "zero_gamma": 5285.0,
  "major_pos_vol": 5350.0,
  "major_pos_oi": 5340.0,
  "major_neg_vol": 5250.0,
  "major_neg_oi": 5260.0,
  "sum_gex_vol": 1234567890.0,
  "sum_gex_oi": 987654321.0,
  "strikes": [
    [5200.0, -500000.0, -450000.0, [-520000, -510000, -490000, -480000, -460000]],
    [5210.0, -300000.0, -280000.0, [-310000, -305000, -295000, -290000, -270000]],
    ...
  ],
  "max_priors": [
    [5350.0, 15000000.0],
    [5340.0, 12000000.0],
    ...
  ]
}
```

### A1.3 Semántica de los Campos de Strike

```
strikes[i] = [strike_price, gex_by_volume, gex_by_oi, [prior_1m, prior_5m, prior_10m, prior_15m, prior_30m]]
```

| Índice | Campo | Tipo | Descripción |
|---|---|---|---|
| `[0]` | Strike price | `double` | Precio de ejercicio de la opción |
| `[1]` | GEX by Volume | `double` | Gamma Exposure calculada por volumen de opciones |
| `[2]` | GEX by OI | `double` | Gamma Exposure calculada por Open Interest |
| `[3][0]` | Prior 1 min | `double` | GEX de este strike hace 1 minuto |
| `[3][1]` | Prior 5 min | `double` | GEX de este strike hace 5 minutos |
| `[3][2]` | Prior 10 min | `double` | GEX de este strike hace 10 minutos |
| `[3][3]` | Prior 15 min | `double` | GEX de este strike hace 15 minutos |
| `[3][4]` | Prior 30 min | `double` | GEX de este strike hace 30 minutos |

### A1.4 Mapeo de Tickers en la API

| Enum en el indicador | Ticker enviado a la API | Notas |
|---|---|---|
| `SPX` | `SPX` | Directo |
| `SPX_ES` | `ES_SPX` | Conversión inversa; indicador aplica factor al chart |
| `NDX_NQ` | `NQ_NDX` | Conversión inversa; indicador aplica factor al chart |
| Cualquier otro | `ToString()` | `QQQ`, `NVDA`, `TSLA`, etc. |

---

## A2. Ciclo de Vida del Indicador en ATAS

```
ATAS Platform                           GexBotAClassic
    │                                       │
    ├── LoadIndicator() ─────────────────▶ Constructor()
    │                                       ├── new GexApiClient()
    │                                       ├── RebuildPens()
    │                                       └── RebuildFonts()
    │
    ├── InitializeIndicator() ───────────▶ OnInitialize()
    │                                       ├── new CancellationTokenSource()
    │                                       └── Task.Run(BackgroundFetchLoopAsync)
    │
    ├── NewBarOrTick() ──────────────────▶ OnCalculate(bar, value)
    │   (cada tick/barra)                   ├── _lastChartPrice = value
    │                                       ├── RecalcAutoFactor()
    │                                       └── TriggerFetch() (una sola vez)
    │
    ├── DrawChart() ─────────────────────▶ OnRender(context, layout)
    │   (cada frame de redraw)              ├── lock(_dataLock) { data = _gexData }
    │                                       ├── DrawHistogram()
    │                                       ├── DrawLookbackDots()
    │                                       ├── DrawStrikeLabels()
    │                                       ├── DrawKeyLevels()
    │                                       ├── DrawInfoPanel()
    │                                       ├── ProcessAlerts()
    │                                       └── DrawAlertOverlay()
    │
    ├── PropertyChanged() ───────────────▶ set { ...; RecalculateValues() }
    │                                       └── Trigger RedrawChart
    │
    └── UnloadIndicator() ───────────────▶ OnDispose()
                                            ├── _cts.Cancel()
                                            ├── _fetchSignal.Set() (desbloquear)
                                            ├── _fetchLoop.Wait(2s)
                                            ├── _cts.Dispose()
                                            ├── _fetchSignal.Dispose()
                                            └── _apiClient.Dispose()
```

---

## A3. Gestión de Errores y Resiliencia

### A3.1 Errores de Red / API

| Escenario | Comportamiento |
|---|---|
| API devuelve error HTTP (4xx, 5xx) | `EnsureSuccessStatusCode()` lanza excepción → catch → devuelve `_cachedClassic` (último dato válido) |
| Timeout de red (>10 seg) | `HttpClient.Timeout` cancela → catch → devuelve cached |
| JSON malformado | `JsonDocument.ParseAsync` falla → `ParseClassicResponse` retorna `null` → se mantiene dato previo |
| API key inválida | Normalmente 401/403 → se captura como error HTTP → se muestra en `_lastError` |
| Sin conexión a internet | DNS/connect fail → catch → cached + se muestra mensaje de error en chart |
| Fetch ya en curso | `SemaphoreSlim.WaitAsync(0)` retorna `false` → devuelve cached sin lanzar segundo request |

### A3.2 Degradación Graceful

El indicador **nunca crashea** ante un error de datos. La cascada de protección es:

```
1. SemaphoreSlim single-flight → evita requests duplicados
2. try/catch en FetchClassicAsync → devuelve cached
3. Null check en OnRender → DrawStatusMessage() si no hay datos
4. Clamp en todos los Range properties → valores siempre válidos
5. LINQ FirstOrDefault + null guards en algoritmos de alerta
```

---

## A4. Anatomía del Histograma

### A4.1 Cálculo de Posición y Tamaño de Barras

```
histRect = panel (1/8, 1/4, 1/3 o 1/2 del ancho del chart)
centerX = histRect.Left + histRect.Width / 2
maxBarHalf = histRect.Width / 2 - 4  (margen de 4px por lado)

Para cada strike:
  y = PriceToY(ApplyMultiplier(strike.Strike))  ← coordenada vertical
  gex = GexByVolume o GexByOi (según config)
  width = ScaleGex(|gex|, maxAbsGex, maxBarHalf)

  Si gex ≥ 0: barra [centerX → centerX + width]  (Call, hacia la derecha)
  Si gex < 0: barra [centerX - width → centerX]   (Put, hacia la izquierda)
```

### A4.2 Escalado

| Modo | Fórmula | Cuándo usarlo |
|---|---|---|
| **Lineal** (default) | `width = (absGex / maxAbsGex) * maxPixels` | Cuando la distribución de gamma es relativamente uniforme |
| **Logarítmico** | `width = log(1 + absGex) / log(1 + maxAbsGex) * maxPixels` | Cuando hay un strike con gamma extrema que aplasta el resto visualmente |

---

## A5. Sistema de Conversión de Precios — Casos de Uso

### A5.1 ¿Por qué es necesario?

Los datos de la API de Gexbot vienen en precios del **subyacente de opciones** (ej. QQQ a $520). Pero el trader opera **futuros** (ej. NQ a $21,000). Sin conversión, los strikes se dibujarían fuera del rango visible del chart.

### A5.2 Tabla de Conversiones Comunes

| Ticker API | Chart | Factor aproximado | Modo recomendado |
|---|---|---|---|
| SPX | ES | ~1.0 (casi 1:1) | None o Auto |
| NDX | NQ | ~1.0 (casi 1:1) | None o Auto |
| QQQ | NQ | ~40 | Auto o Manual (40) |
| SPY | ES | ~10 | Auto o Manual (10) |
| QQQ | QQQ | 1.0 | None |
| NVDA | NVDA | 1.0 | None |

### A5.3 Fórmula de Auto Factor

```
autoFactor = lastChartPrice / data.Spot
```

Se recalcula en:
- `OnCalculate` → cada barra/tick nuevo
- `OnRender` → fallback con precio medio visible
- `BackgroundFetchLoopAsync` → cuando llegan datos nuevos

---

## A6. Detalle Completo del Info Panel

El info panel horizontal se divide en **4 columnas**:

```
┌──────────────┬──────────────┬──────────────┬──────────────┐
│  IDENTIDAD   │  ACTUALIZACIÓN│   NIVELES    │  MAX CHANGE  │
├──────────────┼──────────────┼──────────────┼──────────────┤
│ GexBotA      │ update       │ volume/oi    │ max change   │
│ SPX | Full   │ date  26/06  │ zero gamma   │ curr  5350   │
│ DTE: 0 / 2   │ time  15:30  │ major pos    │ 1m   5320    │
│ Mode: volume │ spot 5320.45 │ major neg    │ 5m   5280    │
│ Auto ×1.0012 │              │ net gex      │ 10m  5350    │
│              │              │              │ 15m  5250    │
│              │              │              │ 30m  5300    │
└──────────────┴──────────────┴──────────────┴──────────────┘
```

| Columna | Datos mostrados |
|---|---|
| **Col 0: Identidad** | Nombre, ticker + aggregation, DTE, modo GEX, factor de conversión |
| **Col 1: Actualización** | Fecha, hora, y precio Spot (con color naranja) |
| **Col 2: Niveles** | Zero Gamma, Major+, Major−, Net GEX (cada uno con su color) |
| **Col 3: Max Change** | Strike con mayor cambio de GEX en cada temporalidad (curr, 1m, 5m, 10m, 15m, 30m) |

---

## A7. Flujo Completo de una Alerta (Diagrama de Secuencia)

```
OnRender()
    │
    ├── ProcessAlerts(data)
    │       │
    │       ├── ¿enableAlerts == false? → return
    │       ├── ¿data.Spot == 0? → return
    │       │
    │       ├── Spot touching Major+/−  (alertas simples)
    │       │
    │       ├── CheckBullishExhaustionAlert(data)
    │       │   ├── ¿cooldown activo? → skip
    │       │   ├── Buscar Major+ strike + GEX value
    │       │   ├── ¿Spot > Major+? → sí
    │       │   ├── LINQ: Take(N) strikes above Spot
    │       │   ├── Sum GEX above
    │       │   ├── ¿sum < threshold% × majorGEX?
    │       │   │   ├── SÍ → _lastExhaustionTime = now
    │       │   │   │       AddAlert("alert", msg)     ──▶ ATAS Alert Window + Sound
    │       │   │   │       ShowAlertOverlay(msg, red)  ──▶ _activeAlertMessage = msg
    │       │   │   └── NO → nada
    │       │   └──
    │       │
    │       ├── CheckMagnetAlert(data)           [similar flow]
    │       ├── CheckLiquidationAlert(data)      [similar flow]
    │       └── CheckNegativeWallAlert(data)     [similar flow]
    │
    └── DrawAlertOverlay(context, fullRect)
            │
            ├── ¿_activeAlertMessage vacío? → return
            ├── ¿DateTime.UtcNow > _alertMessageExpiry? → limpiar, return
            ├── Calcular alpha (fade: 20s full → 10s fadeout)
            ├── Dibujar background box
            ├── Dibujar accent line (color de alerta)
            ├── Dibujar título "⚠ GexBotA Alert"
            └── Dibujar mensaje con word-wrap
```

---

## A8. Matriz de Seguridad y Edge Cases

| Edge Case | Protección implementada |
|---|---|
| API devuelve 0 strikes | `data.Strikes.Count == 0` → `DrawStatusMessage` |
| Menos de 5 strikes above/below para escaneo | `.Take(N)` + `.ToList()` → opera con lo que haya, incluso 0 |
| Major Positive no encontrado en array de strikes | Fallback a `OrderByDescending(GEX).First()` |
| `Math.Abs` en strikes negativos para vacuumPct | Explícito: `double absMajorNegGex = Math.Abs(majorNegGex)` |
| División por zero en porcentajes | Guards: `if (majorPosGex <= 0) return`, `if (totalGammaAbove <= 0) return` |
| Priors array con menos de 4 elementos | `if (majorStrikeData.Priors.Count <= Prior15MinIndex) return` |
| Precio oscila alrededor de Zero Gamma | Edge detection con `_wasAboveZeroGamma` (solo transición) |
| Chart sin `InstrumentInfo` | Fallback: `tickSize = InstrumentInfo != null ? ... : 0.01m` |
| Indicador deshabilitado y reactivado | `_dataLoaded` flag + `TriggerFetch()` fuerza re-fetch |
| Cambio de ticker/aggregation | `_apiClient.ClearCache()` + `TriggerFetch()` |
| Múltiples cambios rápidos de config | `ManualResetEventSlim.Set()` coalesces señales (idempotente) |

---

## A9. Inventario Completo de Propiedades Configurables

### Setup (5 propiedades)

| Propiedad | Tipo | Default | Rango |
|---|---|---|---|
| Gex Type | `GexType` | Volume | Volume / OpenInterest |
| Ticker | `GexTicker` | SPX | 40+ opciones |
| Aggregation | `AggregationPeriod` | Full (90d) | Full / Latest / Next |
| API Key | `string` | "" | — |
| Refresh (sec) | `int` | 1 | 1–600 |

### Conversion (2 propiedades)

| Propiedad | Tipo | Default | Rango |
|---|---|---|---|
| Conversion Mode | `ConversionMode` | None | None / Auto / Manual |
| Manual Factor | `double` | 1.0 | — |

### General Options (8 propiedades)

| Propiedad | Tipo | Default |
|---|---|---|
| Panel Location | `PanelLocation` | Right |
| Panel Size | `PanelSizeOption` | 1/8th |
| Background | `bool` | true |
| Background Color | `Color` | Black |
| Positive Values | `Color` | Green |
| Negative Values | `Color` | Red |
| Bar Width | `int` | 3 (rango 1–12) |
| Show/Color/Style/Width Center Line | 4 props | On, Gray, Dot, 1px |

### Labels (13 propiedades)

| Grupo | Props | Default |
|---|---|---|
| Strike Labels | Show, Color, Bg, FontSize, OffsetX | On, White, Dark, 8pt, 0 |
| Gamma Call Labels | Show, Color, Bg, FontSize, OffsetX | Off, Green, Dark, 7.5pt, 4 |
| Gamma Put Labels | Show, Color, Bg, FontSize, OffsetX | Off, Red, Dark, 7.5pt, 4 |

### Priors Dots (12 propiedades)

| Temporalidad | Show | Color | Size |
|---|---|---|---|
| 1 min | ✅ | Cyan | 3 |
| 5 min | ✅ | Light Blue | 3 |
| 15 min | ✅ | Gold | 4 |
| 30 min | ✅ | Pink | 4 |

### Scaling (1), Majors (6), Misc (5), Info Panel (5)

Total: ~15 propiedades adicionales para líneas de niveles, Spot, Zero Gamma, y panel de datos.

### Alertas (16 propiedades totales)

| Alert | Props | Configurables |
|---|---|---|
| Master | 1 | Enable Alerts (global toggle) |
| Alert 1: Agotamiento | 4 | Enable, Scan Strikes, Threshold%, Cooldown |
| Alert 2: Efecto Imán | 3 | Enable, Concentration%, Cooldown |
| Alert 3: Liquidaciones | 4 | Enable, Drop%, Proximity ticks, Cooldown |
| Alert 4: Muro Negativo | 5 | Enable, Scan Strikes, Vacuum%, Proximity ticks, Cooldown |

---

## A10. Herramienta de Inspección SDK (`_inspector/`)

El proyecto incluye una herramienta auxiliar `_inspector/Program.cs` que usa `MetadataLoadContext` para inspeccionar las DLLs del SDK de ATAS sin cargarlas en el runtime. Esto permite:

- Descubrir tipos, métodos y propiedades disponibles en `OFT.Rendering.dll` y `ATAS.Indicators.dll`.
- Verificar la firma de `DrawingLayouts`, `RenderContext`, `RenderPen`, etc.
- Documentar la API disponible cuando la documentación oficial de ATAS es insuficiente.

Esta herramienta **no se distribuye** con el indicador; es exclusivamente para desarrollo.

---
---

# PARTE 3 — Manual de Usuario

---

## M1. Introducción

### ¿Qué es GexBotA Classic?

GexBotA Classic es un indicador para la plataforma **ATAS** que visualiza la **Gamma Exposure (GEX)** directamente sobre tu gráfico de precios. La Gamma Exposure muestra dónde están posicionados los creadores de mercado (Market Makers) en el mercado de opciones, y cómo sus coberturas fuerzan movimientos de precio predecibles.

### ¿Para qué sirve?

- **Ver los muros:** Identifica niveles de precio donde los Market Makers deben comprar o vender agresivamente para cubrir sus posiciones.
- **Anticipar reversiones:** Detecta cuándo un rally o una caída se está agotando por falta de gamma.
- **Encontrar zonas de rebote:** Localiza soportes "duros" donde la probabilidad de bounce es extrema.
- **Seguir la liquidez:** Observa en tiempo real cómo cambia la gamma en cada strike a lo largo del día.

---

## M2. Instalación

### Requisitos previos

1. **ATAS** instalado y con licencia activa.
2. Una **API Key de Gexbot** válida (obténla en [gexbot.com](https://gexbot.com)).
3. Conexión a Internet.

### Pasos de instalación

1. Descarga el archivo `GexBotA.dll` de la release.
2. Copia el archivo a:
   ```
   %APPDATA%\ATAS\Indicators\
   ```
   (Puedes acceder pegando esa ruta en el Explorador de Windows.)
3. Reinicia ATAS.
4. El indicador aparecerá en la categoría **"GexBot"** al añadir indicadores a un chart.

### Primera configuración

1. Abre un gráfico (ej. futuros ES, NQ, o acciones).
2. Añade el indicador: **Chart → Indicators → GexBot → GexBotA Classic**.
3. En el panel de configuración, ve a **Setup** e introduce tu **API Key**.
4. Selecciona el **Ticker** correspondiente al instrumento que quieres analizar.
5. El perfil de gamma debería aparecer en pocos segundos.

---

## M3. Configuración — Sección por Sección

### M3.1 Setup

| Setting | Qué hace | Recomendación |
|---|---|---|
| **Gex Type** | Elige si calcular la gamma por **Volume** (más reactiva intradía) o por **Open Interest** (más estable, posiciones acumuladas). | `Volume` para scalping/intradía. `Open Interest` para swing. |
| **Ticker** | El activo cuyas opciones quieres analizar. No tiene que ser el mismo que tu chart. | Si operas NQ, selecciona `NDX -> NQ` o `QQQ`. |
| **Aggregation** | Período de expiración de las opciones a agregar. | `90d (agg)` para visión completa. `Latest expiry` para 0DTE. |
| **API Key** | Tu clave personal de acceso a la API de Gexbot. | Cópiala exactamente desde tu cuenta de Gexbot. |
| **Refresh (sec)** | Cada cuántos segundos se pide actualización a la API. | `1` para intradía activo. `5–10` para swing. |

### M3.2 Conversion

> **¿Cuándo necesito esto?** Solo cuando el Ticker de la API y tu chart tienen precios diferentes. Ejemplo: datos de QQQ ($520) pero chart de NQ ($21,000).

| Setting | Qué hace |
|---|---|
| **None** | Sin conversión. Usar cuando el ticker y el chart son el mismo instrumento (ej. QQQ en chart de QQQ). |
| **Auto** | El indicador calcula automáticamente el factor dividiendo el precio del chart entre el Spot de la API. **Recomendado.** |
| **Manual** | Tú introduces un factor fijo (ej. `40` para QQQ→NQ). Útil si Auto no es preciso. |

### M3.3 General Options

| Setting | Qué hace |
|---|---|
| **Panel Location** | ¿El histograma aparece a la derecha o izquierda del chart? |
| **Panel Size** | Ancho del panel: 1/8, 1/4, 1/3 o 1/2 del chart. Empieza con 1/8 y amplía si necesitas más detalle. |
| **Background** | Fondo semi-transparente detrás del histograma para mejorar la legibilidad. |
| **Positive/Negative Values** | Color de las barras positivas (Call gamma) y negativas (Put gamma). |
| **Bar Width** | Grosor de cada barra del histograma. 3 es un buen punto de partida. |
| **Center Line** | Línea vertical que separa visualmente las Calls de las Puts en el histograma. |

### M3.4 Labels

Las labels son textos que aparecen junto a cada barra del histograma.

| Label | Qué muestra | Recomendación |
|---|---|---|
| **Strike Labels** | El precio del strike de la opción (ej. `5320`). Muestra el precio del ticker de opciones, no del chart. | Activar siempre. Te dice exactamente en qué strike estás mirando. |
| **Gamma Call Labels** | El valor de gamma en los strikes con GEX positiva. | Activar si quieres ver los números exactos en la parte de Calls. |
| **Gamma Put Labels** | El valor de gamma en los strikes con GEX negativa. | Activar si quieres ver los números exactos en la parte de Puts. |

Cada tipo de label tiene su propio color, fondo, tamaño de fuente y offset horizontal.

### M3.5 Priors Dots (Lookback Dots)

Los Prior Dots muestran **dónde estaba la gamma** de cada strike hace 1, 5, 15 y 30 minutos. Son los puntitos de colores que aparecen junto a las barras.

| Temporalidad | Color por defecto | ¿Qué te dice? |
|---|---|---|
| **1 min** | Cyan | Cambios muy recientes. Si un dot se aleja mucho de la barra, hubo un movimiento brusco. |
| **5 min** | Azul claro | Tendencia a corto plazo de la gamma. |
| **15 min** | Dorado | Cambio de régimen intradía. |
| **30 min** | Rosa | Contexto amplio. Si todos los dots están lejos de las barras actuales, la estructura cambió mucho. |

> **Truco:** Si los dots de 15–30 min están muy lejos de las barras actuales en el Major Positive, puede significar que los institucionales están liquidando sus posiciones → prepárate para la Alert 3.

### M3.6 Scaling Options

| Setting | Qué hace |
|---|---|
| **Logarithmic Scaling** | Cambia la escala de las barras de lineal a logarítmica. Útil cuando hay un strike con gamma extrema que aplasta visualmente al resto. Actívalo si ves una sola barra gigante y el resto invisible. |

### M3.7 Majors

Controla la visualización de las líneas horizontales Major Positive y Major Negative:

- **Major Positive:** El strike con más gamma positiva (Call). Los Market Makers compran futuros cuando el precio sube hacia aquí. Actúa como "techo de gamma" o imán.
- **Major Negative:** El strike con más gamma negativa (Put). Los Market Makers venden futuros cuando el precio cae hacia aquí. Actúa como "suelo duro".

### M3.8 Misc

| Setting | Qué hace |
|---|---|
| **Zero Gamma Line** | El nivel donde la gamma neta cruza de negativa a positiva. Por encima, los Market Makers amplifican el movimiento; por debajo, lo amortiguan. Es la línea de transición del régimen de mercado. |
| **Spot Line** | El precio actual del subyacente según la API. |

### M3.9 Info Panel

Panel horizontal en la parte superior del chart con 4 columnas de datos:

- **Identidad:** Nombre, ticker, DTE (días hasta expiración), modo.
- **Actualización:** Fecha/hora del último dato y precio Spot.
- **Niveles:** Zero Gamma, Major+, Major−, Net GEX.
- **Max Change:** Qué strike tuvo el mayor cambio de gamma en cada temporalidad.

---

## M4. Alertas — Guía Práctica

### M4.1 Master Toggle

El setting **Enable Alerts** en "Alert 1: Agotamiento" es un toggle maestro. Si está desactivado, **ninguna** alerta se evalúa.

### M4.2 Alert 1: Agotamiento Alcista

> **"No persigas el largo. El rally se está quedando sin gasolina."**

**Cuándo se dispara:**
El precio ha superado el Major Positive, pero los siguientes strikes por encima tienen muy poca gamma. No hay "combustible" para que los Market Makers sigan comprando → el rally probablemente se revertirá.

**Qué hacer cuando suena:**
- NO entres en largo.
- Busca confirmación en tu Order Flow (delta, volumen, etc.) para entrar en corto (Fade).
- El Market Maker ya terminó de cubrir → tomará ganancias.

**Settings:**

| Setting | Default | Explicación simple |
|---|---|---|
| Scan Strikes | 5 | Cuántos strikes mirar por encima. Más strikes = señal más conservadora. |
| Threshold % | 15% | Si la gamma arriba es menos del 15% del Major+, hay vacío. Baja el % para señales más agresivas. |
| Cooldown | 5 min | No repite la alerta en 5 minutos aunque el precio siga ahí. |

**Color del overlay:** 🔴 Rojo.

---

### M4.3 Alert 2: Efecto Imán (Vía Libre)

> **"Mantén el trade abierto. Hay un imán de liquidez arriba."**

**Cuándo se dispara:**
El precio acaba de cruzar el Zero Gamma al alza, y hay un bloque masivo y concentrado de gamma positiva por encima actuando como imán. Los Market Makers **tienen que seguir comprando** futuros hasta llegar a ese nivel.

**Qué hacer cuando suena:**
- Mantén tu posición larga.
- Pon trailing stop en lugar de tomar ganancias prematuras.
- El target del movimiento es el strike indicado en el mensaje.

**Settings:**

| Setting | Default | Explicación simple |
|---|---|---|
| Concentration % | 40% | El strike imán debe representar al menos el 40% de toda la gamma arriba. Más % = solo dispara si el imán es realmente masivo. |
| Cooldown | 10 min | O hasta que el precio caiga de nuevo por debajo del Zero Gamma (se rearma automáticamente). |

**Color del overlay:** 🟢 Verde.

---

### M4.4 Alert 3: Liquidación Institucional

> **"Cuidado: los institucionales están cerrando sus posiciones. El muro se debilita."**

**Cuándo se dispara:**
El precio está operando cerca del Major Positive, y la gamma en ese strike ha caído drásticamente en los últimos 15 minutos. Esto significa que los institucionales están cobrando beneficios y cerrando sus opciones → el muro se desmorona.

**Qué hacer cuando suena:**
- Si estás largo, ajusta el stop o cierra.
- Si la gamma cae más del 50%, el muro ya no existe → prepárate para caída.
- Observa los Prior Dots de 15 min para confirmar visualmente el drop.

**Settings:**

| Setting | Default | Explicación simple |
|---|---|---|
| Drop % | 30% | Si la gamma cayó más del 30% en 15 minutos, alerta. Sube a 50% para señales más conservadoras. |
| Proximity (ticks) | 10 | Solo evalúa si el precio está a 10 ticks o menos del Major+. |
| Cooldown | 15 min | Alerta estructural, no se repite en 15 minutos. |

**Color del overlay:** 🟡 Naranja/Gold.

---

### M4.5 Alert 4: Muro Gamma Negativo

> **"¡Zona de rebote! El Major Negative es la última barrera y no hay nada debajo."**

**Cuándo se dispara:**
El precio está cayendo hacia el Major Negative, y por debajo de ese nivel hay un vacío total de gamma. El Major Negative es el último muro de defensa de los Market Makers → probabilidad extrema de rebote.

**Qué hacer cuando suena:**
- Busca gatillos de compra en tu Order Flow (absorción, delta reversal).
- Pon stop ajustado justo por debajo del Major Negative.
- Si el precio ROMPE el Major Negative con vacío debajo, espera una caída acelerada (el Market Maker deja de defender).

**Settings:**

| Setting | Default | Explicación simple |
|---|---|---|
| Scan Strikes Below | 5 | Cuántos strikes mirar por debajo del Major−. |
| Vacuum % | 10% | Si la gamma debajo es menos del 10% del |Major−|, hay vacío total. |
| Proximity (ticks) | 15 | Solo evalúa si el precio está a 15 ticks o menos encima del Major−. |
| Cooldown | 5 min | Corto, porque los rebotes son rápidos y puedes necesitar la alerta de nuevo. |

**Color del overlay:** 🔵 Azul.

---

## M5. Casos de Uso Comunes

### M5.1 Setup para Scalping de ES (futuros S&P 500)

```
Ticker:        SPX
Aggregation:   Latest expiry (0DTE)
Gex Type:      Volume
Conversion:    Auto
Refresh:       1 segundo
Panel Size:    1/4
```

### M5.2 Setup para Swing en NQ con datos de QQQ

```
Ticker:        QQQ
Aggregation:   90d (agg)
Gex Type:      Open Interest
Conversion:    Auto (o Manual × 40)
Refresh:       5 segundos
Panel Size:    1/8
```

### M5.3 Setup para acciones individuales (NVDA, TSLA, etc.)

```
Ticker:        NVDA
Aggregation:   90d (agg)
Gex Type:      Volume
Conversion:    None
Refresh:       1 segundo
Panel Size:    1/8
```

---

## M6. Lectura del Perfil — Guía Visual

### M6.1 Anatomía del Chart

```
┌────────────────────────────────────────────────────┬───────────┐
│ ┌──────────────────────────────────────────────────┐│           │
│ │ INFO PANEL (4 columnas de datos)                 ││           │
│ └──────────────────────────────────────────────────┘│           │
│                                                    │           │
│  ─ ─ ─ ─ Zero Gamma ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ │ ▓▓▓▓▓▓▓  │
│                                                    │    │      │
│                                                    │ ▓▓▓│      │
│  ════════ Major Positive ═══════════════════════════│▓▓▓▓│▓▓▓▓ │
│                                                    │  ▓▓│▓     │
│  ● ● ●  Spot ● ● ● ● ● ● ● ● ● ● ● ● ● ● ● ● ●│ ▓▓▓│      │
│                                                    │   ▓│▓▓    │
│        [Velas del chart]                           │▓▓▓▓│      │
│                                                    │ ▓▓▓│      │
│                                                    │  ▓▓│▓▓▓   │
│  ════════ Major Negative ═══════════════════════════│▓▓▓▓│▓▓▓▓ │
│                                                    │   ▓│      │
│                                                    │    │      │
│                                                    │ Put│Call  │
│                                                    │  ◄─┼──►   │
└────────────────────────────────────────────────────┴───────────┘
```

### M6.2 ¿Cómo interpretar las barras?

| Dirección | Color | Significado |
|---|---|---|
| **→ Derecha** | Verde | **Call Gamma (positiva).** Los Market Makers comprarán futuros si el precio sube hacia aquí. Actúa como imán/resistencia. |
| **← Izquierda** | Rojo | **Put Gamma (negativa).** Los Market Makers venderán futuros si el precio baja hacia aquí. Actúa como soporte. |
| **Barra más larga** | — | Más gamma = más fuerza del nivel. El strike con la barra verde más larga es el Major Positive. |

### M6.3 ¿Cómo interpretar los dots?

Los dots de colores muestran dónde estaba la gamma de cada strike en el pasado reciente:

- **Dots cerca de la barra actual:** La gamma no ha cambiado mucho → nivel estable.
- **Dots lejos de la barra:** La gamma cambió drásticamente → liquidación o acumulación en ese strike.
- **Dots que desaparecen:** El strike tenía gamma antes y ahora no → institucionales cerraron posiciones.

---

## M7. Preguntas Frecuentes (FAQ)

### ¿Por qué no veo nada en el chart?

1. Verifica que tu **API Key** sea correcta en Setup.
2. Asegúrate de tener **conexión a Internet**.
3. Si estás en un chart de futuros (ES, NQ) con datos de opciones (SPY, QQQ), configura la **Conversión** en Auto o Manual.

### ¿Por qué las barras están todas a la misma altura?

Probablemente tienes un strike con gamma extrema que aplasta el resto. Activa **Logarithmic Scaling** en Scaling Options.

### ¿Puedo usar el indicador en varios charts simultáneamente?

Sí. Cada instancia del indicador tiene su propio `HttpClient` y loop de fondo independiente. Ten en cuenta que cada instancia hará sus propias llamadas a la API.

### ¿Las alertas suenan cuando ATAS está minimizado?

Sí, `AddAlert` de ATAS genera notificaciones del sistema que suenan aunque la ventana esté minimizada.

### ¿Puedo cambiar el ticker sin reiniciar?

Sí. Cambia el Ticker en Setup y los datos se recargan automáticamente en la siguiente actualización (el caché se limpia y se fuerza un re-fetch inmediato).

### ¿Cuántos datos consume de la API?

Cada request al endpoint `/classic/` devuelve ~10–50 KB de JSON dependiendo del ticker. Con refresh de 1 segundo, son ~3–4 MB por hora. El endpoint `/majors/` es mucho más ligero (~500 bytes).

### ¿Funciona con criptomonedas?

Solo si Gexbot soporta el ticker. Actualmente la lista de tickers disponibles incluye índices, ETFs y acciones de EE.UU. Consulta [gexbot.com](https://gexbot.com) para la lista actualizada.

---

## M8. Glosario

| Término | Definición |
|---|---|
| **GEX (Gamma Exposure)** | Mide cuánto tienen que comprar o vender los Market Makers en futuros/acciones para cubrir sus posiciones de opciones cuando el precio se mueve. |
| **Call Gamma** (positiva) | Gamma de opciones Call. Cuando el precio sube hacia un strike con mucha Call Gamma, los MM compran futuros → impulsa el precio al alza. |
| **Put Gamma** (negativa) | Gamma de opciones Put. Cuando el precio baja hacia un strike con mucha Put Gamma, los MM venden futuros → impulsa el precio a la baja. |
| **Major Positive** | El strike con la mayor concentración de Call Gamma. Funciona como imán/resistencia. |
| **Major Negative** | El strike con la mayor concentración de Put Gamma. Funciona como soporte duro. |
| **Zero Gamma** | El nivel de precio donde la gamma neta cruza de negativa a positiva. Marca la transición entre régimen positivo (movimientos amortiguados) y negativo (movimientos amplificados). |
| **Spot** | El precio actual del subyacente según la API de Gexbot. |
| **DTE** | Days To Expiration. Días que faltan para que expiren las opciones. |
| **Net GEX** | Gamma Exposure neta (suma de toda la gamma positiva y negativa). Si es positiva, el mercado está en régimen de "mean reversion"; si es negativa, en régimen de "trend". |
| **Prior / Lookback** | Valor histórico de la gamma en un strike. Los dots muestran cómo ha cambiado la gamma en los últimos 1, 5, 15 y 30 minutos. |
| **Market Maker (MM)** | Institución que provee liquidez vendiendo opciones. Cuando cubren sus posiciones, sus compras/ventas de futuros mueven el precio de forma predecible. |
| **Fade** | Estrategia de trading que va en contra de la tendencia actual, apostando por una reversión. |
| **Vacuum / Vacío** | Zona del mercado sin gamma significativa. Si el precio entra en una zona de vacío, se mueve rápidamente sin oposición. |

---

*Fin del documento. Versión Full-Beta-1.0.*
