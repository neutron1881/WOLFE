# GexBot State Profile

**Version:** 1.1 Beta 1  
**Platform:** ATAS Indicators (.NET 8, C# 12)  
**API:** GexBot Classic Endpoint

---

## 📋 Descripción

**GexBot State Profile** es un indicador compacto tipo dashboard que muestra el **estado del mercado gamma** en tiempo real. Incluye tanto el panel de estado como un **perfil visual de barras GEX** por strike:

- 🎯 **Estado Gamma**: Positivo/Negativo/Neutral
- 📊 **GEX Score**: Puntuación de sentimiento (-100 a +100)
- 📈 **Régimen de Mercado**: Mean-Reverting vs Trending
- 🔔 **Alertas de Cambio de Estado**: Con sonido y popup visual
- 📊 **Perfil GEX**: Barras horizontales por strike (izquierda/derecha/centro)

---

## 🏗️ Arquitectura de Secciones (16 Secciones)

### 📁 CONFIGURACIÓN

| # | Sección | Descripción |
|---|---------|-------------|
| 01 | 🔑 API Configuration | API Key, Ticker, Aggregation, Refresh |
| 02 | 🔄 Price Conversion | Factor de conversión para futuros |

### 📁 PANEL PRINCIPAL

| # | Sección | Descripción |
|---|---------|-------------|
| 03 | 📍 Panel Position | Ubicación del dashboard |
| 04 | 🎯 Gamma State Display | Estado gamma con colores |
| 05 | 📊 Market Metrics | Spot, Zero Gamma, Net GEX |
| 06 | ⚖️ GEX Balance | Score, gauge visual, régimen |
| 07 | 🏷️ Panel Appearance | Colores y estilo del panel |

### 📁 NIVELES EN GRÁFICO

| # | Sección | Descripción |
|---|---------|-------------|
| 08 | ➖ Chart Levels | Líneas ZG, M+, M- |
| 09 | 🔔 State Alerts | Alertas de cambio de estado |
| 10 | 📏 Gamma Zone | Zonas positiva/negativa |

### 📁 AVANZADO

| # | Sección | Descripción |
|---|---------|-------------|
| 11 | 🎨 Themes | Temas de color predefinidos |
| 12 | 📈 Trend Indicator | Indicador de tendencia |
| 13 | 📜 State History | Historial de estados |
| 14 | 💾 Data Export | Exportación a CSV/TXT |
| 15 | 🔌 API Diagnostics | Diagnóstico de conexión |
| 16 | 📊 GEX Profile | **Perfil de barras horizontales** |

---

## 📊 Perfil GEX (Sección 16)

El perfil GEX muestra barras horizontales por cada strike, similar al indicador Classic pero integrado con el dashboard de estado.

### Opciones de Posición

| Posición | Descripción |
|----------|-------------|
| **Left** | Barras en el lado izquierdo del gráfico |
| **Right** | Barras en el lado derecho del gráfico |
| **Center** | Barras centradas en el gráfico |

### Configuración

| Opción | Descripción | Rango |
|--------|-------------|-------|
| Show GEX Profile | Activa/desactiva el perfil | On/Off |
| Profile Position | Izquierda/Derecha/Centro | Left/Right/Center |
| Profile Offset | Distancia desde el borde | 0-500 px |
| Max Bar Width | Ancho máximo de barras | 50-400 px |
| Bar Thickness | Grosor de cada barra | 2-20 px |
| Positive Bar Color | Color barras positivas | Magenta (default) |
| Negative Bar Color | Color barras negativas | Cyan (default) |
| Bar Fill Opacity | Transparencia del relleno | 50-255 |
| Show Bar Outline | Muestra contorno | On/Off |
| Show Center Line | Línea central de referencia | On/Off |
| Show Bar Values | Muestra valores numéricos | On/Off |
| Strike Range (%) | Porcentaje de strikes visibles | 1-20% |

### Visualización

```
                    │
     ████████████   │                      ← GEX Positivo (derecha)
         █████████  │                      
           ███████  │                      
    ██████████████  │                      ← Strike con mayor GEX+
              ████  │                      
─────────────────── │ ─────────────────── ← Línea central
                    │  ██████████████      ← GEX Negativo (izquierda)
                    │  ████████████        
                    │  █████████           
                    │  ████████████████    ← Strike con mayor GEX-
                    │  ██████████          
                    │
```

---

## 🎯 Estados Gamma

El indicador calcula el estado basándose en la posición del **Spot** respecto al **Zero Gamma**:

| Estado | Condición | Significado | Régimen |
|--------|-----------|-------------|---------|
| 🟢 **Positive** | Spot > ZG + zona neutral | Dealers tienen gamma positivo | Mean-Reverting |
| 🔴 **Negative** | Spot < ZG - zona neutral | Dealers tienen gamma negativo | Trending |
| 🟡 **Neutral** | Spot ≈ ZG (±zona) | En transición | Unknown |

### Zona Neutral Configurable
- Puedes ajustar el tamaño de la "zona neutral" en puntos
- Por defecto: 5 puntos alrededor del Zero Gamma
- Útil para evitar oscilaciones frecuentes cerca del ZG

---

## 📊 GEX Score

Puntuación de -100 a +100 basada en la posición del precio entre los niveles Major:

```
Score = ((Spot - MajorNeg) / (MajorPos - MajorNeg) * 200) - 100
```

- **+100**: Precio en Major Positive (máximo gamma positivo)
- **0**: Precio equidistante entre Major+ y Major-
- **-100**: Precio en Major Negative (máximo gamma negativo)

---

## 📦 Endpoint API Utilizado

El indicador usa el endpoint **classic** completo para obtener los strikes:

```
GET https://api.gexbot.com/{TICKER}/classic/{AGGREGATION}?key={API_KEY}
```

**Response JSON:**
```json
{
  "timestamp": 1753283591,
  "ticker": "SPX",
  "spot": 6326.45,
  "major_pos_vol": 6339.92,
  "major_pos_oi": 6400,
  "major_neg_vol": 6300,
  "major_neg_oi": 6200,
  "zero_gamma": 6323.39,
  "sum_gex_vol": 41199.07077,
  "sum_gex_oi": 57505.37699,
  "strikes": [
    [6300, -15000.5, -12000.3],
    [6310, -8000.2, -6500.1],
    [6320, 5000.8, 4200.5],
    [6330, 12000.3, 10500.2],
    [6340, 18000.7, 15000.4]
  ]
}
```

---

## 🎨 Panel Dashboard

Vista del panel principal:

```
┌──────────────────────────────────────┐
│  🟢 GAMMA POSITIVE                   │  ← Header con color del estado
├──────────────────────────────────────┤
│  Spot:        6326.50                │
│  Zero Gamma:  6323.80                │
│  Distance:    +2.70 pts              │
│  Major +:     6340.00                │
│  Major -:     6315.00                │
│  Net GEX:     +41.2K                 │
├──────────────────────────────────────┤
│  GEX Score:   +67                    │
│  [██████████████░░░░░░] +67/100      │  ← Gauge visual
├──────────────────────────────────────┤
│  📊 Mean-Reverting                   │  ← Régimen de mercado
│  Trend: ▲ Bullish                    │  ← Indicador de tendencia
├──────────────────────────────────────┤
│  Updated: 14:32:15                   │
└──────────────────────────────────────┘
```

---

## 🔔 Sistema de Alertas

### Alertas de Cambio de Estado

1. **Trigger**: Cuando el estado gamma cambia (Pos→Neg, Neg→Pos, etc.)
2. **Sonido**: Archivos WAV configurables por estado
3. **Popup**: Notificación visual con fade-out

### Configuración de Sonidos
```
Positive Alert Sound: C:\Windows\Media\chimes.wav
Negative Alert Sound: C:\Windows\Media\chord.wav
```

### Popup de Alerta
- Aparece centrado en el gráfico
- Muestra el cambio de estado
- Desaparece gradualmente (fade-out)
- Duración configurable (1-30 segundos)

---

## 📏 Zonas Gamma en Gráfico

El indicador puede dibujar zonas de color en el gráfico:

- **Zona Positiva** (arriba de ZG): Color verde semitransparente
- **Zona Negativa** (abajo de ZG): Color rojo semitransparente
- **Zona Neutral**: Banda alrededor de ZG (opcional)

Las zonas ayudan a visualizar rápidamente en qué territorio gamma está el precio.

---

## 🎨 Temas Disponibles

| Tema | Descripción |
|------|-------------|
| **Dark** | Fondo oscuro, colores vibrantes (default) |
| **Light** | Fondo claro para monitores brillantes |
| **GreenRed** | Verdes/rojos intensos para trading |
| **BlueOrange** | Azul/naranja para daltonismo |
| **Custom** | Mantiene colores personalizados |

---

## 💾 Exportación de Datos

Exporta automáticamente el estado a CSV o TXT:

**Formato CSV:**
```csv
Timestamp,Ticker,State,Spot,ZeroGamma,GexScore,Regime
2024-01-15 14:30:00,SPY,Positive,490.25,489.50,+45,MeanReverting
2024-01-15 14:35:00,SPY,Positive,490.80,489.50,+52,MeanReverting
```

**Configuración:**
- Intervalo de exportación: 1-60 minutos
- Ruta personalizable o Documents por defecto
- Nombre: `GexState_{TICKER}_{YYYYMMDD}.csv`

---

## 🔧 Configuración Recomendada

### Para Trading Intradía (0DTE)
```
Ticker: SPY o SPX
Aggregation: zero
Refresh: 30 segundos
Neutral Zone: 3-5 puntos
Alerts: Enabled (Positive + Negative)
```

### Para Swing Trading
```
Ticker: SPY o QQQ
Aggregation: full
Refresh: 60 segundos
Neutral Zone: 10 puntos
Show History Panel: Enabled
```

---

## 📈 Diferencias con GexBot Classic Profile

| Aspecto | Classic Profile | State Profile |
|---------|----------------|---------------|
| **Visualización** | Histograma por strike | Panel dashboard |
| **Datos** | Todos los strikes + priors | Solo majors + estado |
| **Uso** | Análisis detallado | Vista rápida |
| **Complejidad** | 23 secciones | 15 secciones |
| **Endpoint** | /classic | /classic/majors |
| **Peso en memoria** | Mayor (muchos datos) | Menor (datos mínimos) |

---

## 🐛 Troubleshooting

### "API Key not configured"
- Añade tu API Key en la sección 01

### "Connection error"
- Verifica tu conexión a internet
- Revisa que la API Key sea válida
- Activa el panel de diagnósticos (Sección 15)

### Estado no cambia
- Aumenta la zona neutral si oscila mucho
- Reduce si no detecta cambios

### Precio no coincide con el gráfico
- Activa "Price Conversion" y "Auto-Calculate Factor"
- Para futuros (NQ/ES) ajusta el factor manualmente

---

## 📜 Changelog

### v1.0 Beta 1
- ✅ Implementación inicial de 15 secciones
- ✅ Dashboard con estado gamma, score, régimen
- ✅ Zonas gamma en gráfico
- ✅ Alertas de cambio de estado con sonido
- ✅ Historial de estados
- ✅ 4 temas de color predefinidos
- ✅ Exportación a CSV/TXT
- ✅ Panel de diagnósticos API

---

## 📄 Licencia

Desarrollado para uso con la API de GexBot.
Requiere suscripción Classic de GexBot.
