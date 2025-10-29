using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
 [DisplayName("Trigger")]
 public class Trigger : Indicator
 {
 public enum MarkerShape { Circle, Square, Triangle, Diamond }

 // Settings comunes
 private bool _enableBuy = true;
 private bool _enableSell = false;
 private bool _useInstrumentTickSize = true;
 private decimal _tickSizeFallback =0.25m;
 private bool _onlyWhenTrue = true;

 // Marker BUY
 private int _offsetTicks =10;
 private MarkerShape _shapeBuy = MarkerShape.Triangle;
 private int _sizePx =12;
 private Color _fillColorBuy = Color.MediumSeaGreen;
 private Color _borderColorBuy = Color.DarkGreen;

 // Marker SELL
 private int _sellOffsetTicks =10;
 private MarkerShape _shapeSell = MarkerShape.Triangle;
 private int _sizeSellPx =12;
 private Color _fillColorSell = Color.OrangeRed;
 private Color _borderColorSell = Color.DarkRed;

 // Comunes
 private int _borderThickness =1;
 private int _opacity =220;

 // Condiciones BUY (por defecto todas requeridas)
 private bool _buyReqCloseAboveMid = true;
 private bool _buyReqPrevCloseAboveMid = true;
 private bool _buyReqPrevPocBelowMid = true;
 private bool _buyReqCurrPocBelowMid = true;
 private bool _buyReqPrevDeltaPositive = true;
 private bool _buyReqPrevDeltaNegative;
 private bool _buyReqCurrDeltaPositive = true;
 private bool _buyReqCurrDeltaNegative;
 private bool _buyRequireBullBody;

 // Condiciones SELL (por defecto todas requeridas)
 private bool _sellReqCloseBelowMid = true;
 private bool _sellReqPrevCloseBelowMid = true;
 private bool _sellReqPrevPocAboveMid = true;
 private bool _sellReqCurrPocAboveMid = true;
 private bool _sellReqPrevDeltaPositive = true;
 private bool _sellReqPrevDeltaNegative;
 private bool _sellReqCurrDeltaPositive = true;
 private bool _sellReqCurrDeltaNegative;
 private bool _sellRequireBearBody;

 private readonly HashSet<int> _buyBars = new();
 private readonly HashSet<int> _sellBars = new();

 public Trigger()
 {
 EnableCustomDrawing = true;
 DenyToChangePanel = true;
 SubscribeToDrawingEvents(DrawingLayouts.Historical);
 }

 //1. General
 [Category("1. General"), Display(Name = "Enable Buy", Order =10)]
 public bool EnableBuy { get => _enableBuy; set { _enableBuy = value; RecalculateValues(); RedrawChart(); } }

 [Category("1. General"), Display(Name = "Enable Sell", Order =20)]
 public bool EnableSell { get => _enableSell; set { _enableSell = value; RecalculateValues(); RedrawChart(); } }

 [Category("1. General"), Display(Name = "Use instrument tick size", Order =30)]
 public bool UseInstrumentTickSize { get => _useInstrumentTickSize; set { _useInstrumentTickSize = value; RecalculateValues(); RedrawChart(); } }

 [Category("1. General"), Display(Name = "Tick size (fallback)", Order =40)]
 public decimal TickSizeFallback { get => _tickSizeFallback; set { _tickSizeFallback = value <=0 ?0.25m : value; RecalculateValues(); RedrawChart(); } }

 [Category("1. General"), Display(Name = "Mark only when true", Order =50)]
 public bool MarkOnlyWhenTrue { get => _onlyWhenTrue; set { _onlyWhenTrue = value; RecalculateValues(); RedrawChart(); } }

 //2. Marker (Buy)
 [Category("2. Marker (Buy)"), Display(Name = "Offset (ticks)", Order =10)]
 [Range(0,500)]
 public int OffsetTicks { get => _offsetTicks; set { _offsetTicks = Math.Clamp(value,0,500); RedrawChart(); } }

 [Category("2. Marker (Buy)"), Display(Name = "Shape", Order =20)]
 public MarkerShape BuyShape { get => _shapeBuy; set { _shapeBuy = value; RedrawChart(); } }

 [Category("2. Marker (Buy)"), Display(Name = "Size (px)", Order =30)]
 [Range(4,80)]
 public int SizePx { get => _sizePx; set { _sizePx = Math.Clamp(value,4,80); RedrawChart(); } }

 [Category("2. Marker (Buy)"), Display(Name = "Fill color", Order =40)]
 public Color BuyFillColor { get => _fillColorBuy; set { _fillColorBuy = value; RedrawChart(); } }

 [Category("2. Marker (Buy)"), Display(Name = "Border color", Order =50)]
 public Color BuyBorderColor { get => _borderColorBuy; set { _borderColorBuy = value; RedrawChart(); } }

 //3. Marker (Sell)
 [Category("3. Marker (Sell)"), Display(Name = "Offset (ticks)", Order =10)]
 [Range(0,500)]
 public int SellOffsetTicks { get => _sellOffsetTicks; set { _sellOffsetTicks = Math.Clamp(value,0,500); RedrawChart(); } }

 [Category("3. Marker (Sell)"), Display(Name = "Shape", Order =20)]
 public MarkerShape SellShape { get => _shapeSell; set { _shapeSell = value; RedrawChart(); } }

 [Category("3. Marker (Sell)"), Display(Name = "Size (px)", Order =30)]
 [Range(4,80)]
 public int SellSizePx { get => _sizeSellPx; set { _sizeSellPx = Math.Clamp(value,4,80); RedrawChart(); } }

 [Category("3. Marker (Sell)"), Display(Name = "Fill color", Order =40)]
 public Color SellFillColor { get => _fillColorSell; set { _fillColorSell = value; RedrawChart(); } }

 [Category("3. Marker (Sell)"), Display(Name = "Border color", Order =50)]
 public Color SellBorderColor { get => _borderColorSell; set { _borderColorSell = value; RedrawChart(); } }

 //4. Marker (Common)
 [Category("4. Marker (Common)"), Display(Name = "Border thickness", Order =10)]
 [Range(0,10)]
 public int BorderThickness { get => _borderThickness; set { _borderThickness = Math.Clamp(value,0,10); RedrawChart(); } }

 [Category("4. Marker (Common)"), Display(Name = "Opacity (0-255)", Order =20)]
 [Range(0,255)]
 public int Opacity { get => _opacity; set { _opacity = Math.Clamp(value,0,255); RedrawChart(); } }

 //5. Conditions (Buy)
 [Category("5. Conditions (Buy)"), Display(Name = "Require current close >50%", Order =10)]
 public bool Buy_RequireCloseAboveMid { get => _buyReqCloseAboveMid; set { _buyReqCloseAboveMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require previous close >50%", Order =20)]
 public bool Buy_RequirePrevCloseAboveMid { get => _buyReqPrevCloseAboveMid; set { _buyReqPrevCloseAboveMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require previous POC <50%", Order =30)]
 public bool Buy_RequirePrevPocBelowMid { get => _buyReqPrevPocBelowMid; set { _buyReqPrevPocBelowMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require current POC <50%", Order =40)]
 public bool Buy_RequireCurrPocBelowMid { get => _buyReqCurrPocBelowMid; set { _buyReqCurrPocBelowMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require prev Delta >0", Order =50)]
 public bool Buy_RequirePrevDeltaPositive { get => _buyReqPrevDeltaPositive; set { _buyReqPrevDeltaPositive = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require prev Delta <0", Order =60)]
 public bool Buy_RequirePrevDeltaNegative { get => _buyReqPrevDeltaNegative; set { _buyReqPrevDeltaNegative = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require curr Delta >0", Order =70)]
 public bool Buy_RequireCurrDeltaPositive { get => _buyReqCurrDeltaPositive; set { _buyReqCurrDeltaPositive = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require curr Delta <0", Order =80)]
 public bool Buy_RequireCurrDeltaNegative { get => _buyReqCurrDeltaNegative; set { _buyReqCurrDeltaNegative = value; RecalculateValues(); RedrawChart(); } }

 [Category("5. Conditions (Buy)"), Display(Name = "Require bullish body", Order =90)]
 public bool Buy_RequireBullBody { get => _buyRequireBullBody; set { _buyRequireBullBody = value; RecalculateValues(); RedrawChart(); } }

 //6. Conditions (Sell)
 [Category("6. Conditions (Sell)"), Display(Name = "Require current close <50%", Order =10)]
 public bool Sell_RequireCloseBelowMid { get => _sellReqCloseBelowMid; set { _sellReqCloseBelowMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require previous close <50%", Order =20)]
 public bool Sell_RequirePrevCloseBelowMid { get => _sellReqPrevCloseBelowMid; set { _sellReqPrevCloseBelowMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require previous POC >50%", Order =30)]
 public bool Sell_RequirePrevPocAboveMid { get => _sellReqPrevPocAboveMid; set { _sellReqPrevPocAboveMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require current POC >50%", Order =40)]
 public bool Sell_RequireCurrPocAboveMid { get => _sellReqCurrPocAboveMid; set { _sellReqCurrPocAboveMid = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require prev Delta >0", Order =50)]
 public bool Sell_RequirePrevDeltaPositive { get => _sellReqPrevDeltaPositive; set { _sellReqPrevDeltaPositive = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require prev Delta <0", Order =60)]
 public bool Sell_RequirePrevDeltaNegative { get => _sellReqPrevDeltaNegative; set { _sellReqPrevDeltaNegative = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require curr Delta >0", Order =70)]
 public bool Sell_RequireCurrDeltaPositive { get => _sellReqCurrDeltaPositive; set { _sellReqCurrDeltaPositive = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require curr Delta <0", Order =80)]
 public bool Sell_RequireCurrDeltaNegative { get => _sellReqCurrDeltaNegative; set { _sellReqCurrDeltaNegative = value; RecalculateValues(); RedrawChart(); } }

 [Category("6. Conditions (Sell)"), Display(Name = "Require bearish body", Order =90)]
 public bool Sell_RequireBearBody { get => _sellRequireBearBody; set { _sellRequireBearBody = value; RecalculateValues(); RedrawChart(); } }

 protected override void OnRecalculate()
 {
 _buyBars.Clear();
 _sellBars.Clear();
 base.OnRecalculate();
 }

 protected override void OnCalculate(int bar, decimal value)
 {
 if (bar <=0)
 {
 _buyBars.Remove(bar);
 _sellBars.Remove(bar);
 return;
 }

 var c = GetCandle(bar);
 var p = GetCandle(bar -1);
 if (c is null || p is null || c.High <= c.Low || p.High <= p.Low)
 {
 _buyBars.Remove(bar);
 _sellBars.Remove(bar);
 return;
 }

 var tick = (_useInstrumentTickSize && InstrumentInfo != null && InstrumentInfo.TickSize >0)
 ? InstrumentInfo.TickSize
 : _tickSizeFallback;
 if (tick <=0) tick =0.25m;

 var mid = (c.High + c.Low) /2m;
 var prevMid = (p.High + p.Low) /2m;
 var prevDelta = TryGetCandleDelta(p, tick);
 var currDelta = TryGetCandleDelta(c, tick);

 bool buy = false;
 bool sell = false;

 if (_enableBuy)
 {
 buy = true;
 if (_buyReqCloseAboveMid) buy &= c.Close > mid;
 if (_buyReqPrevCloseAboveMid) buy &= p.Close > prevMid;
 if (_buyReqPrevPocBelowMid)
 {
 var prevPoc = TryGetPocPrice(p, tick);
 buy &= prevPoc.HasValue && prevPoc.Value < prevMid;
 }
 if (_buyReqCurrPocBelowMid)
 {
 var currPoc = TryGetPocPrice(c, tick);
 buy &= currPoc.HasValue && currPoc.Value < mid;
 }
 if (_buyReqPrevDeltaPositive) buy &= prevDelta >0;
 if (_buyReqPrevDeltaNegative) buy &= prevDelta <0;
 if (_buyReqCurrDeltaPositive) buy &= currDelta >0;
 if (_buyReqCurrDeltaNegative) buy &= currDelta <0;
 if (_buyRequireBullBody) buy &= c.Close > c.Open;
 }

 if (_enableSell && !buy)
 {
 sell = true;
 if (_sellReqCloseBelowMid) sell &= c.Close < mid;
 if (_sellReqPrevCloseBelowMid) sell &= p.Close < prevMid;
 if (_sellReqPrevPocAboveMid)
 {
 var prevPoc = TryGetPocPrice(p, tick);
 sell &= prevPoc.HasValue && prevPoc.Value > prevMid;
 }
 if (_sellReqCurrPocAboveMid)
 {
 var currPoc = TryGetPocPrice(c, tick);
 sell &= currPoc.HasValue && currPoc.Value > mid;
 }
 if (_sellReqPrevDeltaPositive) sell &= prevDelta >0;
 if (_sellReqPrevDeltaNegative) sell &= prevDelta <0;
 if (_sellReqCurrDeltaPositive) sell &= currDelta >0;
 if (_sellReqCurrDeltaNegative) sell &= currDelta <0;
 if (_sellRequireBearBody) sell &= c.Close < c.Open;
 }

 if (buy)
 {
 _buyBars.Add(bar);
 _sellBars.Remove(bar);
 }
 else if (sell)
 {
 _sellBars.Add(bar);
 _buyBars.Remove(bar);
 }
 else
 {
 if (_onlyWhenTrue)
 {
 _buyBars.Remove(bar);
 _sellBars.Remove(bar);
 }
 }
 }

 protected override void OnRender(RenderContext context, DrawingLayouts layout)
 {
 if (ChartInfo?.PriceChartContainer == null) return;

 var tick = (_useInstrumentTickSize && InstrumentInfo != null && InstrumentInfo.TickSize >0)
 ? InstrumentInfo.TickSize
 : _tickSizeFallback;
 if (tick <=0) tick =0.25m;

 int first = FirstVisibleBarNumber;
 int last = LastVisibleBarNumber;
 int sizeBuy = _sizePx;
 int sizeSell = _sizeSellPx;
 var penBuyBase = _borderThickness >0 ? new RenderPen(_borderColorBuy, _borderThickness) : null;
 var penSellBase = _borderThickness >0 ? new RenderPen(_borderColorSell, _borderThickness) : null;
 var fillBuy = Color.FromArgb(_opacity, _fillColorBuy);
 var fillSell = Color.FromArgb(_opacity, _fillColorSell);

 foreach (var bar in _buyBars)
 {
 if (bar < first || bar > last) continue;
 var c = GetCandle(bar); if (c is null) continue;
 var price = c.Low - _offsetTicks * tick;
 int x = ChartInfo.PriceChartContainer.GetXByBar(bar, false);
 int y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);

 RenderPen? penToUse = penBuyBase;
 if (penToUse == null && _borderThickness ==0 && bar >=2)
 {
 var pp = GetCandle(bar -2);
 if (pp != null && pp.Close < pp.Open)
 penToUse = new RenderPen(_borderColorBuy,1);
 }

 DrawingUp(context, x, y, sizeBuy, fillBuy, penToUse);
 }

 foreach (var bar in _sellBars)
 {
 if (bar < first || bar > last) continue;
 var c = GetCandle(bar); if (c is null) continue;
 var price = c.High + _sellOffsetTicks * tick;
 int x = ChartInfo.PriceChartContainer.GetXByBar(bar, false);
 int y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);

 RenderPen? penToUse = penSellBase;
 if (penToUse == null && _borderThickness ==0 && bar >=2)
 {
 var pp = GetCandle(bar -2);
 if (pp != null && pp.Close > pp.Open)
 penToUse = new RenderPen(_borderColorSell,1);
 }

 DrawingDown(context, x, y, sizeSell, fillSell, penToUse);
 }
 }

 private decimal? TryGetPocPrice(IndicatorCandle candle, decimal tick)
 {
 try
 {
 if (candle == null || tick <=0) return null;
 var low = candle.Low; var high = candle.High; if (high <= low) return null;
 decimal? poc = null; decimal maxVol = -1m;
 for (var price = low; price <= high; price += tick)
 {
 var info = candle.GetPriceVolumeInfo(price); if (info == null) continue; var vol = info.Volume; if (vol > maxVol) { maxVol = vol; poc = price; }
 }
 return poc;
 }
 catch { return null; }
 }

 private decimal TryGetCandleDelta(IndicatorCandle candle, decimal tick)
 {
 try
 {
 if (candle == null || tick <=0)
 return 0m;

 var low = candle.Low;
 var high = candle.High;
 if (high <= low)
 return 0m;

 decimal ask =0m, bid =0m;
 for (var price = low; price <= high; price += tick)
 {
 var info = candle.GetPriceVolumeInfo(price); if (info == null) continue; ask += info.Ask; bid += info.Bid;
 }
 return ask - bid;
 }
 catch
 {
 return 0m;
 }
 }

 // Flechas estilo DeltaTurnaround
 private void DrawingUp(RenderContext ctx, int centerX, int centerY, int size, Color fill, RenderPen? pen)
 {
 int half = Math.Max(2, size /2);
 // punta arriba y cuerpo
 var p1 = new Point(centerX, centerY - half -2);
 var p2 = new Point(centerX - half, centerY + half /2);
 var p3 = new Point(centerX - half /2, centerY + half /2);
 var p4 = new Point(centerX - half /2, centerY + half +2);
 var p5 = new Point(centerX + half /2, centerY + half +2);
 var p6 = new Point(centerX + half /2, centerY + half /2);
 var p7 = new Point(centerX + half, centerY + half /2);
 var poly = new[] { p1, p2, p3, p4, p5, p6, p7 };
 ctx.FillPolygon(fill, poly);
 if (pen != null) ctx.DrawPolygon(pen, poly);
 }

 private void DrawingDown(RenderContext ctx, int centerX, int centerY, int size, Color fill, RenderPen? pen)
 {
 int half = Math.Max(2, size /2);
 // punta abajo y cuerpo
 var p1 = new Point(centerX, centerY + half +2);
 var p2 = new Point(centerX - half, centerY - half /2);
 var p3 = new Point(centerX - half /2, centerY - half /2);
 var p4 = new Point(centerX - half /2, centerY - half -2);
 var p5 = new Point(centerX + half /2, centerY - half -2);
 var p6 = new Point(centerX + half /2, centerY - half /2);
 var p7 = new Point(centerX + half, centerY - half /2);
 var poly = new[] { p1, p2, p3, p4, p5, p6, p7 };
 ctx.FillPolygon(fill, poly);
 if (pen != null) ctx.DrawPolygon(pen, poly);
 }

 // Mantengo DrawMarker por compatibilidad si se llegara a usar en otro lado
 private void DrawMarker(RenderContext ctx, int centerX, int centerY, int size, Color fill, RenderPen? pen, MarkerShape shape, bool inverted)
 {
 int half = size /2;
 var rect = new Rectangle(centerX - half, centerY - half, size, size);

 switch (shape)
 {
 case MarkerShape.Circle:
 ctx.FillEllipse(fill, rect);
 if (pen != null) ctx.DrawEllipse(pen, rect);
 break;
 case MarkerShape.Square:
 ctx.FillRectangle(fill, rect);
 if (pen != null) ctx.DrawRectangle(pen, rect);
 break;
 case MarkerShape.Triangle:
 if (!inverted) DrawingUp(ctx, centerX, centerY, size, fill, pen); else DrawingDown(ctx, centerX, centerY, size, fill, pen);
 break;
 case MarkerShape.Diamond:
 var d1 = new Point(centerX, centerY - half);
 var d2 = new Point(centerX + half, centerY);
 var d3 = new Point(centerX, centerY + half);
 var d4 = new Point(centerX - half, centerY);
 ctx.FillPolygon(fill, new[] { d1, d2, d3, d4 });
 if (pen != null) ctx.DrawPolygon(pen, new[] { d1, d2, d3, d4 });
 break;
 }
 }
 }
}
