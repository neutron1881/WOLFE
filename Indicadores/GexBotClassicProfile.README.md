# GexBot Classic Profile - ATAS Indicator

## 📋 Descripción

**GexBot Classic Profile** es un indicador avanzado para ATAS que visualiza datos de Gamma Exposure (GEX) en tiempo real desde la API de GexBot. Permite analizar el posicionamiento de opciones de los market makers para identificar niveles clave de soporte, resistencia y zonas de volatilidad.

---

## 🏗️ Arquitectura de Menús

El indicador está organizado en **5 categorías principales** con **23 secciones** de configuración:

### 📁 CATEGORÍA 1: CONFIGURACIÓN (Secciones 01-02)

| Sección | Descripción |
|---------|-------------|
| **01. 🔑 API Configuration** | Configuración de conexión: API Key, Ticker, Agregación DTE, Intervalo de refresco |
| **02. 🔄 Price Conversion** | Conversión de precios entre spot y chart (futuros), factor de conversión, redondeo |

### 📁 CATEGORÍA 2: VISUALIZACIÓN (Secciones 03-08)

| Sección | Descripción |
|---------|-------------|
| **03. 📍 Profile Position** | Posición del perfil GEX, línea central, offset |
| **04. 📊 Bar Appearance** | Apariencia de barras: ancho, grosor, colores positivo/negativo, opacidad |
| **05. 🏷️ Labels & Values** | Etiquetas de valores GEX y strikes, tamaño de fuente, colores |
| **06. ➖ Major Levels** | Niveles principales: Zero Gamma, Major Positive, Major Negative |
| **07. 📋 Info Panel** | Panel de información principal con resumen de datos GEX |
| **08. 📐 Strike Grid** | Cuadrícula de strikes con líneas horizontales y etiquetas |

### 📁 CATEGORÍA 3: ANÁLISIS AVANZADO (Secciones 09-13)

| Sección | Descripción |
|---------|-------------|
| **09. 🔔 Alerts** | Sistema de alertas por cambios significativos en GEX |
| **10. ➡️ Direction Arrows** | Flechas de dirección comparando GEX actual vs. histórico |
| **11. 🌈 Gamma Zones** | Zonas de gamma positivo/negativo con coloreado de fondo |
| **12. 📈 Sparklines** | Mini-gráficos de tendencia para cada strike |
| **13. 📜 Alert History** | Historial de alertas generadas con timestamps |

### 📁 CATEGORÍA 4: HERRAMIENTAS AVANZADAS (Secciones 14-20)

| Sección | Descripción |
|---------|-------------|
| **14. 🔊 Sound Alerts** | Alertas sonoras (beeps o WAV personalizado) |
| **15. 🎯 Strike Filter** | Filtrado de strikes por rango, distancia del spot o cantidad |
| **16. ⏱️ Multi-Timeframe** | Panel comparativo multi-período (1min, 5min, 10min, 15min, 30min) |
| **17. 💾 Data Export** | Exportación de datos a CSV/TXT con timestamps |
| **18. 🧲 GEX Clusters** | Detección automática de clusters de GEX (zonas S/R) |
| **19. 📊 Statistics Panel** | Panel estadístico: media, desviación, percentiles, GEX Score |
| **20. 🎯 Price Targets** | Proyección de objetivos de precio basados en niveles GEX |

### 📁 CATEGORÍA 5: CONFIGURACIÓN AVANZADA (Secciones 21-23)

| Sección | Descripción |
|---------|-------------|
| **21. ⏰ Session Filter** | Filtro de sesión de mercado, indicador 0DTE, progreso de sesión |
| **22. 🎨 Theme Presets** | Presets de tema: Dark, Light, High Contrast, Classic, Neon |
| **23. 🔌 API Diagnostics** | Diagnósticos de API: latencia, errores, log de actividad |

---

## 📖 Guía de Configuración por Sección

### 01. 🔑 API Configuration

| Propiedad | Descripción | Valores |
|-----------|-------------|---------|
| `API Key` | Clave de acceso a GexBot API | String (requerido) |
| `Ticker` | Símbolo a consultar | QQQ, SPY, SPX, NDX, NQ_NDX, ES_SPX, AAPL, NVDA, TSLA, AMD, AMZN, META, MSFT, GOOGL |
| `Aggregation (DTE)` | Agregación por días hasta expiración | full (todos), zero (0DTE), one (1DTE) |
| `Refresh (sec)` | Intervalo de actualización | 1-3600 segundos |
| `GEX Data Source` | Fuente de datos GEX | Volume, OpenInterest |

### 02. 🔄 Price Conversion

| Propiedad | Descripción | Valores |
|-----------|-------------|---------|
| `Enable price conversion` | Activar conversión de precios | true/false |
| `Conversion factor` | Factor multiplicador chart/spot | Decimal > 0 |
| `Auto-calculate factor` | Calcular factor automáticamente | true/false |
| `Price step (rounding)` | Paso de redondeo de precios | Decimal > 0 (ej: 0.25) |

### 06. ➖ Major Levels

Los niveles principales son:
- **Zero Gamma (ZG)**: Nivel donde gamma neto = 0. Por encima = gamma positivo (soporte), por debajo = gamma negativo (volatilidad).
- **Major Positive**: Strike con mayor GEX positivo (resistencia fuerte).
- **Major Negative**: Strike con mayor GEX negativo (soporte de volatilidad).

### 09. 🔔 Alerts

| Tipo de Umbral | Descripción |
|----------------|-------------|
| `Percentage` | Alerta cuando cambio % supera umbral |
| `AbsoluteValue` | Alerta cuando cambio absoluto supera umbral |
| `TopN` | Alerta para los N mayores cambios |

### 18. 🧲 GEX Clusters

Los clusters detectan zonas de strikes consecutivos con GEX significativo:
- **Clusters Positivos**: Zonas de resistencia (dealers long gamma)
- **Clusters Negativos**: Zonas de soporte/volatilidad (dealers short gamma)

### 19. 📊 Statistics Panel

| Métrica | Descripción |
|---------|-------------|
| `Mean` | Media de valores GEX |
| `Std Dev` | Desviación estándar |
| `P25/P50/P75` | Percentiles 25, 50 (mediana), 75 |
| `Pos/Neg Ratio` | Ratio GEX positivo / negativo |
| `GEX Score` | Puntuación -100 a +100 (sentimiento) |

### 22. 🎨 Theme Presets

| Tema | Estilo |
|------|--------|
| `Custom` | Mantiene configuración actual |
| `Dark` | Tema oscuro profesional |
| `Light` | Tema claro para fondos blancos |
| `HighContrast` | Alto contraste (accesibilidad) |
| `Classic` | Estilo clásico |
| `Neon` | Colores neón vibrantes |

---

## 🔧 Uso Típico

### Para Futuros (NQ, ES)
1. Seleccionar ticker: `NQ_NDX` o `ES_SPX`
2. Habilitar conversión de precios
3. Usar auto-calculate factor = true

### Para ETFs/Acciones
1. Seleccionar ticker: `QQQ`, `SPY`, o acción individual
2. Deshabilitar conversión de precios (factor = 1.0)

### Para 0DTE Trading
1. Aggregation = `zero`
2. Habilitar `Highlight 0DTE` en Session Filter
3. Refresh = 15-30 segundos para datos frescos

---

## 📊 Interpretación de GEX

| Condición | Significado | Comportamiento Esperado |
|-----------|-------------|-------------------------|
| Precio > Zero Gamma | Gamma Positivo | Mercado estable, dealers compran dips, venden rallies |
| Precio < Zero Gamma | Gamma Negativo | Mayor volatilidad, movimientos amplificados |
| Cerca de Major Positive | Resistencia fuerte | Precio tiende a rebotar hacia abajo |
| Cerca de Major Negative | Soporte de volatilidad | Zona de posible reversal |

---

## 📡 API Endpoints

El indicador utiliza la API de GexBot:
```
https://api.gexbot.com/{ticker}/classic/{aggregation}?key={apiKey}
```

### Respuesta JSON
```json
{
  "timestamp": 1699999999999,
  "ticker": "QQQ",
  "spot": 500.50,
  "zero_gamma": 498.00,
  "major_pos_vol": 505.00,
  "major_neg_vol": 495.00,
  "sum_gex_vol": 1500000000,
  "strikes": [
    [500, 250000000, 180000000, [240000000, 235000000, 230000000, 225000000, 220000000]]
  ]
}
```

---

## 🔄 Changelog

### v2.0 Beta 2 (Current)
- ✅ 23 secciones de configuración
- ✅ Detección de clusters GEX
- ✅ Panel de estadísticas con GEX Score
- ✅ Proyección de objetivos de precio
- ✅ Filtro de sesión de mercado
- ✅ Presets de temas visuales
- ✅ Diagnósticos de API

### v1.0
- Perfil GEX básico
- Niveles principales (ZG, Major Pos/Neg)
- Panel de información

---

## 📝 Licencia

Desarrollado para uso con ATAS Trading Platform.
API proporcionada por GexBot (requiere suscripción).

---

## 🐛 Troubleshooting

| Problema | Solución |
|----------|----------|
| "API Key is required" | Configurar API Key válida en sección 01 |
| Precios no coinciden | Habilitar conversión y verificar factor |
| Sin datos | Verificar conexión a internet, revisar API Diagnostics |
| Latencia alta | Aumentar intervalo de refresh, verificar conexión |

