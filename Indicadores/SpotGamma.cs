using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("SpotGammaLevels")]
    public class SpotGammaLevels : Indicator
    {
        #region Structures
        private struct Nivel
        {
            public string Etiqueta;
            public decimal Precio;
            public Color Color;
            public int Grosor;
            public DashStyle Estilo;
            public int TamanoTexto;
        }

        private struct Zona
        {
            public decimal PrecioAlto;
            public decimal PrecioBajo;
            public string Etiqueta;
            public Color Color;
            public int Grosor;
            public DashStyle Estilo;
            public int TamanoTexto;
        }
        #endregion

        #region Manual Levels (siempre primeros en el menú)
        // Nivel 1
        private bool _m1Enabled;
        private decimal _m1Price;
        private string _m1Label = "Manual 1";
        private Color _m1Color = Color.Cyan;
        private int _m1Thickness = 2;
        private DashStyle _m1Style = DashStyle.Solid;
        private int _m1FontSize = 9;

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Activado", Order = 1)]
        public bool Manual1Enabled { get => _m1Enabled; set { _m1Enabled = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Precio", Order = 2)]
        public decimal Manual1Price { get => _m1Price; set { _m1Price = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Etiqueta", Order = 3)]
        public string Manual1Label { get => _m1Label; set { _m1Label = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Color", Order = 4)]
        public Color Manual1Color { get => _m1Color; set { _m1Color = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Grosor", Order = 5)]
        [Range(1, 10)]
        public int Manual1Thickness { get => _m1Thickness; set { _m1Thickness = Math.Max(1, value); UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Estilo", Order = 6)]
        public DashStyle Manual1Style { get => _m1Style; set { _m1Style = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 1 - Tamaño texto", Order = 7)]
        [Range(6, 60)]
        public int Manual1FontSize { get => _m1FontSize; set { _m1FontSize = Math.Max(6, value); UpdateIndicator(); } }

        // Nivel 2
        private bool _m2Enabled;
        private decimal _m2Price;
        private string _m2Label = "Manual 2";
        private Color _m2Color = Color.Magenta;
        private int _m2Thickness = 2;
        private DashStyle _m2Style = DashStyle.Solid;
        private int _m2FontSize = 9;

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Activado", Order = 11)]
        public bool Manual2Enabled { get => _m2Enabled; set { _m2Enabled = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Precio", Order = 12)]
        public decimal Manual2Price { get => _m2Price; set { _m2Price = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Etiqueta", Order = 13)]
        public string Manual2Label { get => _m2Label; set { _m2Label = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Color", Order = 14)]
        public Color Manual2Color { get => _m2Color; set { _m2Color = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Grosor", Order = 15)]
        [Range(1, 10)]
        public int Manual2Thickness { get => _m2Thickness; set { _m2Thickness = Math.Max(1, value); UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Estilo", Order = 16)]
        public DashStyle Manual2Style { get => _m2Style; set { _m2Style = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 2 - Tamaño texto", Order = 17)]
        [Range(6, 60)]
        public int Manual2FontSize { get => _m2FontSize; set { _m2FontSize = Math.Max(6, value); UpdateIndicator(); } }

        // Nivel 3
        private bool _m3Enabled;
        private decimal _m3Price;
        private string _m3Label = "Manual 3";
        private Color _m3Color = Color.Orange;
        private int _m3Thickness = 2;
        private DashStyle _m3Style = DashStyle.Solid;
        private int _m3FontSize = 9;

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Activado", Order = 21)]
        public bool Manual3Enabled { get => _m3Enabled; set { _m3Enabled = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Precio", Order = 22)]
        public decimal Manual3Price { get => _m3Price; set { _m3Price = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Etiqueta", Order = 23)]
        public string Manual3Label { get => _m3Label; set { _m3Label = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Color", Order = 24)]
        public Color Manual3Color { get => _m3Color; set { _m3Color = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Grosor", Order = 25)]
        [Range(1, 10)]
        public int Manual3Thickness { get => _m3Thickness; set { _m3Thickness = Math.Max(1, value); UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Estilo", Order = 26)]
        public DashStyle Manual3Style { get => _m3Style; set { _m3Style = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 3 - Tamaño texto", Order = 27)]
        [Range(6, 60)]
        public int Manual3FontSize { get => _m3FontSize; set { _m3FontSize = Math.Max(6, value); UpdateIndicator(); } }

        // Nivel 4
        private bool _m4Enabled;
        private decimal _m4Price;
        private string _m4Label = "Manual 4";
        private Color _m4Color = Color.Lime;
        private int _m4Thickness = 2;
        private DashStyle _m4Style = DashStyle.Solid;
        private int _m4FontSize = 9;

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Activado", Order = 31)]
        public bool Manual4Enabled { get => _m4Enabled; set { _m4Enabled = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Precio", Order = 32)]
        public decimal Manual4Price { get => _m4Price; set { _m4Price = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Etiqueta", Order = 33)]
        public string Manual4Label { get => _m4Label; set { _m4Label = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Color", Order = 34)]
        public Color Manual4Color { get => _m4Color; set { _m4Color = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Grosor", Order = 35)]
        [Range(1, 10)]
        public int Manual4Thickness { get => _m4Thickness; set { _m4Thickness = Math.Max(1, value); UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Estilo", Order = 36)]
        public DashStyle Manual4Style { get => _m4Style; set { _m4Style = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 4 - Tamaño texto", Order = 37)]
        [Range(6, 60)]
        public int Manual4FontSize { get => _m4FontSize; set { _m4FontSize = Math.Max(6, value); UpdateIndicator(); } }

        // Nivel 5
        private bool _m5Enabled;
        private decimal _m5Price;
        private string _m5Label = "Manual 5";
        private Color _m5Color = Color.Yellow;
        private int _m5Thickness = 2;
        private DashStyle _m5Style = DashStyle.Solid;
        private int _m5FontSize = 9;

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Activado", Order = 41)]
        public bool Manual5Enabled { get => _m5Enabled; set { _m5Enabled = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Precio", Order = 42)]
        public decimal Manual5Price { get => _m5Price; set { _m5Price = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Etiqueta", Order = 43)]
        public string Manual5Label { get => _m5Label; set { _m5Label = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Color", Order = 44)]
        public Color Manual5Color { get => _m5Color; set { _m5Color = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Grosor", Order = 45)]
        [Range(1, 10)]
        public int Manual5Thickness { get => _m5Thickness; set { _m5Thickness = Math.Max(1, value); UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Estilo", Order = 46)]
        public DashStyle Manual5Style { get => _m5Style; set { _m5Style = value; UpdateIndicator(); } }

        [Display(GroupName = "0. Manual Levels", Name = "Nivel 5 - Tamaño texto", Order = 47)]
        [Range(6, 60)]
        public int Manual5FontSize { get => _m5FontSize; set { _m5FontSize = Math.Max(6, value); UpdateIndicator(); } }
        #endregion

        #region Configurable Properties
        private string _filePath = @"C:\Path\To\ThinkOrSwim.txt";
        [Display(GroupName = "1. Settings", Name = "File Path", Order = 0)]
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                UpdateIndicator();
            }
        }

        private decimal _groupThreshold = 10;
        [Display(GroupName = "1. Settings", Name = "Group Threshold", Order = 1)]
        public decimal GroupThreshold
        {
            get => _groupThreshold;
            set
            {
                _groupThreshold = value;
                UpdateIndicator();
            }
        }

        private bool _mostrarEtiquetas = true;
        [Display(GroupName = "2. Appearance", Name = "Show Labels", Order = 10)]
        public bool MostrarEtiquetas
        {
            get => _mostrarEtiquetas;
            set
            {
                _mostrarEtiquetas = value;
                UpdateIndicator();
            }
        }

        private int _dateLabelOffsetY = 10;
        [Display(GroupName = "2. Appearance", Name = "Date label offset Y (px)", Order = 12)]
        [Range(-2000, 2000)]
        public int DateLabelOffsetY
        {
            get => _dateLabelOffsetY;
            set
            {
                _dateLabelOffsetY = Math.Clamp(value, -2000, 2000);
                UpdateIndicator();
            }
        }

        public enum DateLabelAlignment { Left, Center, Right }
        private DateLabelAlignment _dateAlignment = DateLabelAlignment.Left;
        [Display(GroupName = "2. Appearance", Name = "Date label alignment", Order = 13)]
        public DateLabelAlignment DateAlignment
        {
            get => _dateAlignment;
            set
            {
                _dateAlignment = value;
                UpdateIndicator();
            }
        }

        private int _labelOffsetX = 10;
        [Display(GroupName = "2. Appearance", Name = "Label Horizontal Offset", Order = 11)]
        public int LabelOffsetX
        {
            get => _labelOffsetX;
            set
            {
                _labelOffsetX = value;
                UpdateIndicator();
            }
        }
        #endregion

        #region Labels
        private string _labelCW = "CallWall";
        [Display(GroupName = "3. Labels", Name = "CW Label", Order = 20)]
        public string LabelCW
        {
            get => _labelCW;
            set
            {
                _labelCW = value;
                UpdateIndicator();
            }
        }

        private string _labelC1 = "ComboL1";
        [Display(GroupName = "3. Labels", Name = "C1 Label", Order = 21)]
        public string LabelC1
        {
            get => _labelC1;
            set
            {
                _labelC1 = value;
                UpdateIndicator();
            }
        }

        private string _labelC2 = "ComboL2";
        [Display(GroupName = "3. Labels", Name = "C2 Label", Order = 22)]
        public string LabelC2
        {
            get => _labelC2;
            set
            {
                _labelC2 = value;
                UpdateIndicator();
            }
        }

        private string _labelC3 = "ComboL3";
        [Display(GroupName = "3. Labels", Name = "C3 Label", Order = 23)]
        public string LabelC3
        {
            get => _labelC3;
            set
            {
                _labelC3 = value;
                UpdateIndicator();
            }
        }

        private string _labelC4 = "ComboL4";
        [Display(GroupName = "3. Labels", Name = "C4 Label", Order = 24)]
        public string LabelC4
        {
            get => _labelC4;
            set
            {
                _labelC4 = value;
                UpdateIndicator();
            }
        }

        private string _labelL1 = "L1";
        [Display(GroupName = "3. Labels", Name = "L1 Label", Order = 25)]
        public string LabelL1
        {
            get => _labelL1;
            set
            {
                _labelL1 = value;
                UpdateIndicator();
            }
        }

        private string _labelL2 = "L2";
        [Display(GroupName = "3. Labels", Name = "L2 Label", Order = 26)]
        public string LabelL2
        {
            get => _labelL2;
            set
            {
                _labelL2 = value;
                UpdateIndicator();
            }
        }

        private string _labelL3 = "L3";
        [Display(GroupName = "3. Labels", Name = "L3 Label", Order = 27)]
        public string LabelL3
        {
            get => _labelL3;
            set
            {
                _labelL3 = value;
                UpdateIndicator();
            }
        }

        private string _labelL4 = "L4";
        [Display(GroupName = "3. Labels", Name = "L4 Label", Order = 28)]
        public string LabelL4
        {
            get => _labelL4;
            set
            {
                _labelL4 = value;
                UpdateIndicator();
            }
        }

        private string _labelPW = "PutWall";
        [Display(GroupName = "3. Labels", Name = "PW Label", Order = 29)]
        public string LabelPW
        {
            get => _labelPW;
            set
            {
                _labelPW = value;
                UpdateIndicator();
            }
        }

        private string _labelVT = "VolTrig";
        [Display(GroupName = "3. Labels", Name = "VT Label", Order = 30)]
        public string LabelVT
        {
            get => _labelVT;
            set
            {
                _labelVT = value;
                UpdateIndicator();
            }
        }

        private string _labelZG = "ZeroGamma";
        [Display(GroupName = "3. Labels", Name = "ZG Label", Order = 31)]
        public string LabelZG
        {
            get => _labelZG;
            set
            {
                _labelZG = value;
                UpdateIndicator();
            }
        }
        #endregion

        #region Appearance Per Level
        private Color _colorCW = Color.FromArgb(255, 240, 128, 128);
        [Display(GroupName = "4. CW", Name = "Color", Order = 40)]
        public Color ColorCW
        {
            get => _colorCW;
            set
            {
                _colorCW = value;
                UpdateIndicator();
            }
        }

        private int _cwLineThickness = 1;
        [Display(GroupName = "4. CW", Name = "Line Thickness", Order = 41)]
        [Range(1, 10)]
        public int CWLineThickness
        {
            get => _cwLineThickness;
            set
            {
                _cwLineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _cwLineStyle = DashStyle.Solid;
        [Display(GroupName = "4. CW", Name = "Line Style", Order = 42)]
        public DashStyle CWLineStyle
        {
            get => _cwLineStyle;
            set
            {
                _cwLineStyle = value;
                UpdateIndicator();
            }
        }

        private int _cwFontSize = 8;
        [Display(GroupName = "4. CW", Name = "Font Size", Order = 43)]
        [Range(6, 60)]
        public int CWFontSize
        {
            get => _cwFontSize;
            set
            {
                _cwFontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorC1 = Color.Cyan;
        [Display(GroupName = "5. C1", Name = "Color", Order = 50)]
        public Color ColorC1
        {
            get => _colorC1;
            set
            {
                _colorC1 = value;
                UpdateIndicator();
            }
        }

        private int _c1LineThickness = 1;
        [Display(GroupName = "5. C1", Name = "Line Thickness", Order = 51)]
        [Range(1, 10)]
        public int C1LineThickness
        {
            get => _c1LineThickness;
            set
            {
                _c1LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _c1LineStyle = DashStyle.Solid;
        [Display(GroupName = "5. C1", Name = "Line Style", Order = 52)]
        public DashStyle C1LineStyle
        {
            get => _c1LineStyle;
            set
            {
                _c1LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _c1FontSize = 8;
        [Display(GroupName = "5. C1", Name = "Font Size", Order = 53)]
        [Range(6, 60)]
        public int C1FontSize
        {
            get => _c1FontSize;
            set
            {
                _c1FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorC2 = Color.Cyan;
        [Display(GroupName = "6. C2", Name = "Color", Order = 60)]
        public Color ColorC2
        {
            get => _colorC2;
            set
            {
                _colorC2 = value;
                UpdateIndicator();
            }
        }

        private int _c2LineThickness = 1;
        [Display(GroupName = "6. C2", Name = "Line Thickness", Order = 61)]
        [Range(1, 10)]
        public int C2LineThickness
        {
            get => _c2LineThickness;
            set
            {
                _c2LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _c2LineStyle = DashStyle.Solid;
        [Display(GroupName = "6. C2", Name = "Line Style", Order = 62)]
        public DashStyle C2LineStyle
        {
            get => _c2LineStyle;
            set
            {
                _c2LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _c2FontSize = 8;
        [Display(GroupName = "6. C2", Name = "Font Size", Order = 63)]
        [Range(6, 60)]
        public int C2FontSize
        {
            get => _c2FontSize;
            set
            {
                _c2FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorC3 = Color.Cyan;
        [Display(GroupName = "7. C3", Name = "Color", Order = 70)]
        public Color ColorC3
        {
            get => _colorC3;
            set
            {
                _colorC3 = value;
                UpdateIndicator();
            }
        }

        private int _c3LineThickness = 1;
        [Display(GroupName = "7. C3", Name = "Line Thickness", Order = 71)]
        [Range(1, 10)]
        public int C3LineThickness
        {
            get => _c3LineThickness;
            set
            {
                _c3LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _c3LineStyle = DashStyle.Solid;
        [Display(GroupName = "7. C3", Name = "Line Style", Order = 72)]
        public DashStyle C3LineStyle
        {
            get => _c3LineStyle;
            set
            {
                _c3LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _c3FontSize = 8;
        [Display(GroupName = "7. C3", Name = "Font Size", Order = 73)]
        [Range(6, 60)]
        public int C3FontSize
        {
            get => _c3FontSize;
            set
            {
                _c3FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorC4 = Color.Cyan;
        [Display(GroupName = "8. C4", Name = "Color", Order = 80)]
        public Color ColorC4
        {
            get => _colorC4;
            set
            {
                _colorC4 = value;
                UpdateIndicator();
            }
        }

        private int _c4LineThickness = 1;
        [Display(GroupName = "8. C4", Name = "Line Thickness", Order = 81)]
        [Range(1, 10)]
        public int C4LineThickness
        {
            get => _c4LineThickness;
            set
            {
                _c4LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _c4LineStyle = DashStyle.Solid;
        [Display(GroupName = "8. C4", Name = "Line Style", Order = 82)]
        public DashStyle C4LineStyle
        {
            get => _c4LineStyle;
            set
            {
                _c4LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _c4FontSize = 8;
        [Display(GroupName = "8. C4", Name = "Font Size", Order = 83)]
        [Range(6, 60)]
        public int C4FontSize
        {
            get => _c4FontSize;
            set
            {
                _c4FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorL1 = Color.Gray;
        [Display(GroupName = "9. L1", Name = "Color", Order = 90)]
        public Color ColorL1
        {
            get => _colorL1;
            set
            {
                _colorL1 = value;
                UpdateIndicator();
            }
        }

        private int _l1LineThickness = 1;
        [Display(GroupName = "9. L1", Name = "Line Thickness", Order = 91)]
        [Range(1, 10)]
        public int L1LineThickness
        {
            get => _l1LineThickness;
            set
            {
                _l1LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _l1LineStyle = DashStyle.Solid;
        [Display(GroupName = "9. L1", Name = "Line Style", Order = 92)]
        public DashStyle L1LineStyle
        {
            get => _l1LineStyle;
            set
            {
                _l1LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _l1FontSize = 8;
        [Display(GroupName = "9. L1", Name = "Font Size", Order = 93)]
        [Range(6, 60)]
        public int L1FontSize
        {
            get => _l1FontSize;
            set
            {
                _l1FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorL2 = Color.Gray;
        [Display(GroupName = "10. L2", Name = "Color", Order = 100)]
        public Color ColorL2
        {
            get => _colorL2;
            set
            {
                _colorL2 = value;
                UpdateIndicator();
            }
        }

        private int _l2LineThickness = 1;
        [Display(GroupName = "10. L2", Name = "Line Thickness", Order = 101)]
        [Range(1, 10)]
        public int L2LineThickness
        {
            get => _l2LineThickness;
            set
            {
                _l2LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _l2LineStyle = DashStyle.Solid;
        [Display(GroupName = "10. L2", Name = "Line Style", Order = 102)]
        public DashStyle L2LineStyle
        {
            get => _l2LineStyle;
            set
            {
                _l2LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _l2FontSize = 8;
        [Display(GroupName = "10. L2", Name = "Font Size", Order = 103)]
        [Range(6, 60)]
        public int L2FontSize
        {
            get => _l2FontSize;
            set
            {
                _l2FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorL3 = Color.Gray;
        [Display(GroupName = "11. L3", Name = "Color", Order = 110)]
        public Color ColorL3
        {
            get => _colorL3;
            set
            {
                _colorL3 = value;
                UpdateIndicator();
            }
        }

        private int _l3LineThickness = 1;
        [Display(GroupName = "11. L3", Name = "Line Thickness", Order = 111)]
        [Range(1, 10)]
        public int L3LineThickness
        {
            get => _l3LineThickness;
            set
            {
                _l3LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _l3LineStyle = DashStyle.Solid;
        [Display(GroupName = "11. L3", Name = "Line Style", Order = 112)]
        public DashStyle L3LineStyle
        {
            get => _l3LineStyle;
            set
            {
                _l3LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _l3FontSize = 8;
        [Display(GroupName = "11. L3", Name = "Font Size", Order = 113)]
        [Range(6, 60)]
        public int L3FontSize
        {
            get => _l3FontSize;
            set
            {
                _l3FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorL4 = Color.Gray;
        [Display(GroupName = "12. L4", Name = "Color", Order = 120)]
        public Color ColorL4
        {
            get => _colorL4;
            set
            {
                _colorL4 = value;
                UpdateIndicator();
            }
        }

        private int _l4LineThickness = 1;
        [Display(GroupName = "12. L4", Name = "Line Thickness", Order = 121)]
        [Range(1, 10)]
        public int L4LineThickness
        {
            get => _l4LineThickness;
            set
            {
                _l4LineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _l4LineStyle = DashStyle.Solid;
        [Display(GroupName = "12. L4", Name = "Line Style", Order = 122)]
        public DashStyle L4LineStyle
        {
            get => _l4LineStyle;
            set
            {
                _l4LineStyle = value;
                UpdateIndicator();
            }
        }

        private int _l4FontSize = 8;
        [Display(GroupName = "12. L4", Name = "Font Size", Order = 123)]
        [Range(6, 60)]
        public int L4FontSize
        {
            get => _l4FontSize;
            set
            {
                _l4FontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorPW = Color.Green;
        [Display(GroupName = "13. PW", Name = "Color", Order = 130)]
        public Color ColorPW
        {
            get => _colorPW;
            set
            {
                _colorPW = value;
                UpdateIndicator();
            }
        }

        private int _pwLineThickness = 1;
        [Display(GroupName = "13. PW", Name = "Line Thickness", Order = 131)]
        [Range(1, 10)]
        public int PWLineThickness
        {
            get => _pwLineThickness;
            set
            {
                _pwLineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _pwLineStyle = DashStyle.Solid;
        [Display(GroupName = "13. PW", Name = "Line Style", Order = 132)]
        public DashStyle PWLineStyle
        {
            get => _pwLineStyle;
            set
            {
                _pwLineStyle = value;
                UpdateIndicator();
            }
        }

        private int _pwFontSize = 8;
        [Display(GroupName = "13. PW", Name = "Font Size", Order = 133)]
        [Range(6, 60)]
        public int PWFontSize
        {
            get => _pwFontSize;
            set
            {
                _pwFontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorVT = Color.Orange;
        [Display(GroupName = "14. VT", Name = "Color", Order = 140)]
        public Color ColorVT
        {
            get => _colorVT;
            set
            {
                _colorVT = value;
                UpdateIndicator();
            }
        }

        private int _vtLineThickness = 1;
        [Display(GroupName = "14. VT", Name = "Line Thickness", Order = 141)]
        [Range(1, 10)]
        public int VTLineThickness
        {
            get => _vtLineThickness;
            set
            {
                _vtLineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _vtLineStyle = DashStyle.Solid;
        [Display(GroupName = "14. VT", Name = "Line Style", Order = 142)]
        public DashStyle VTLineStyle
        {
            get => _vtLineStyle;
            set
            {
                _vtLineStyle = value;
                UpdateIndicator();
            }
        }

        private int _vtFontSize = 8;
        [Display(GroupName = "14. VT", Name = "Font Size", Order = 143)]
        [Range(6, 60)]
        public int VTFontSize
        {
            get => _vtFontSize;
            set
            {
                _vtFontSize = value;
                UpdateIndicator();
            }
        }

        private Color _colorZG = Color.White;
        [Display(GroupName = "15. ZG", Name = "Color", Order = 150)]
        public Color ColorZG
        {
            get => _colorZG;
            set
            {
                _colorZG = value;
                UpdateIndicator();
            }
        }

        private int _zgLineThickness = 1;
        [Display(GroupName = "15. ZG", Name = "Line Thickness", Order = 151)]
        [Range(1, 10)]
        public int ZGLineThickness
        {
            get => _zgLineThickness;
            set
            {
                _zgLineThickness = value;
                UpdateIndicator();
            }
        }

        private DashStyle _zgLineStyle = DashStyle.Solid;
        [Display(GroupName = "15. ZG", Name = "Line Style", Order = 152)]
        public DashStyle ZGLineStyle
        {
            get => _zgLineStyle;
            set
            {
                _zgLineStyle = value;
                UpdateIndicator();
            }
        }

        private int _zgFontSize = 8;
        [Display(GroupName = "15. ZG", Name = "Font Size", Order = 153)]
        [Range(6, 60)]
        public int ZGFontSize
        {
            get => _zgFontSize;
            set
            {
                _zgFontSize = value;
                UpdateIndicator();
            }
        }
        #endregion

        private readonly List<Nivel> _niveles = new List<Nivel>();
        private readonly Dictionary<string, decimal> _levels = new Dictionary<string, decimal>();
        private bool _loaded = false;
        private string _errorMessage = string.Empty;
        private string _fileDateText = string.Empty;
        private readonly string[] _levelOrder = new[] { "cw", "c1", "c2", "c3", "c4", "l1", "l2", "l3", "l4", "pw", "vt", "zg" };

        public SpotGammaLevels()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
        }

        #region Main Logic
        protected override void OnInitialize()
        {
            var currentSymbol = InstrumentInfo?.Instrument?.ToUpperInvariant() ?? Instrument?.ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrEmpty(currentSymbol))
            {
                _errorMessage = "No se pudo obtener el instrumento del gráfico.";
                // Aun así, construimos niveles manuales
                UpdateIndicator();
                return;
            }

            LoadLevels(currentSymbol);
            UpdateIndicator(); // rellena _niveles (manuales + archivo si hay)
        }

        private void AddManualLevels()
        {
            // Agrega niveles manuales activos a _niveles
            void Add(bool enabled, decimal price, string label, Color color, int thick, DashStyle style, int fontSize)
            {
                if (!enabled) return;
                // No hay validación de precio - se acepta cualquier valor decimal

                _niveles.Add(new Nivel
                {
                    Etiqueta = label ?? string.Empty,
                    Precio = price,
                    Color = color,
                    Grosor = Math.Max(1, thick),
                    Estilo = style,
                    TamanoTexto = Math.Max(6, fontSize)
                });
            }

            Add(_m1Enabled, _m1Price, _m1Label, _m1Color, _m1Thickness, _m1Style, _m1FontSize);
            Add(_m2Enabled, _m2Price, _m2Label, _m2Color, _m2Thickness, _m2Style, _m2FontSize);
            Add(_m3Enabled, _m3Price, _m3Label, _m3Color, _m3Thickness, _m3Style, _m3FontSize);
            Add(_m4Enabled, _m4Price, _m4Label, _m4Color, _m4Thickness, _m4Style, _m4FontSize);
            Add(_m5Enabled, _m5Price, _m5Label, _m5Color, _m5Thickness, _m5Style, _m5FontSize);
        }

        private bool HasAnyManualEnabled() =>
            _m1Enabled || _m2Enabled || _m3Enabled || _m4Enabled || _m5Enabled;

        private void UpdateIndicator()
        {
            // Reconstruye lista de niveles: primero manuales, luego archivo (si cargado)
            _niveles.Clear();

            // 1) Manuales primero
            AddManualLevels();

            // 2) Archivo si está cargado
            if (_loaded)
            {
                foreach (var key in _levelOrder)
                {
                    if (!_levels.ContainsKey(key) || double.IsNaN((double)_levels[key]))
                        continue;

                    decimal price = _levels[key];
                    _niveles.Add(new Nivel
                    {
                        Etiqueta = GetLabelByKey(key),
                        Precio = price,
                        Color = GetColorByKey(key),
                        Grosor = GetThicknessByKey(key),
                        Estilo = GetStyleByKey(key),
                        TamanoTexto = GetFontSizeByKey(key)
                    });
                }
            }

            // Fuerza redibujado
            RedrawChart();
        }

        private string GetLabelByKey(string key) => key.ToLower() switch
        {
            "cw" => LabelCW,
            "c1" => LabelC1,
            "c2" => LabelC2,
            "c3" => LabelC3,
            "c4" => LabelC4,
            "l1" => LabelL1,
            "l2" => LabelL2,
            "l3" => LabelL3,
            "l4" => LabelL4,
            "pw" => LabelPW,
            "vt" => LabelVT,
            "zg" => LabelZG,
            _ => key.ToUpper()
        };

        private Color GetColorByKey(string key) => key.ToLower() switch
        {
            "cw" => ColorCW,
            "c1" => ColorC1,
            "c2" => ColorC2,
            "c3" => ColorC3,
            "c4" => ColorC4,
            "l1" => ColorL1,
            "l2" => ColorL2,
            "l3" => ColorL3,
            "l4" => ColorL4,
            "pw" => ColorPW,
            "vt" => ColorVT,
            "zg" => ColorZG,
            _ => Color.Gray
        };

        private int GetThicknessByKey(string key) => key.ToLower() switch
        {
            "cw" => CWLineThickness,
            "c1" => C1LineThickness,
            "c2" => C2LineThickness,
            "c3" => C3LineThickness,
            "c4" => C4LineThickness,
            "l1" => L1LineThickness,
            "l2" => L2LineThickness,
            "l3" => L3LineThickness,
            "l4" => L4LineThickness,
            "pw" => PWLineThickness,
            "vt" => VTLineThickness,
            "zg" => ZGLineThickness,
            _ => 1
        };

        private DashStyle GetStyleByKey(string key) => key.ToLower() switch
        {
            "cw" => CWLineStyle,
            "c1" => C1LineStyle,
            "c2" => C2LineStyle,
            "c3" => C3LineStyle,
            "c4" => C4LineStyle,
            "l1" => L1LineStyle,
            "l2" => L2LineStyle,
            "l3" => L3LineStyle,
            "l4" => L4LineStyle,
            "pw" => PWLineStyle,
            "vt" => VTLineStyle,
            "zg" => ZGLineStyle,
            _ => DashStyle.Solid
        };

        private int GetFontSizeByKey(string key) => key.ToLower() switch
        {
            "cw" => CWFontSize,
            "c1" => C1FontSize,
            "c2" => C2FontSize,
            "c3" => C3FontSize,
            "c4" => C4FontSize,
            "l1" => L1FontSize,
            "l2" => L2FontSize,
            "l3" => L3FontSize,
            "l4" => L4FontSize,
            "pw" => PWFontSize,
            "vt" => VTFontSize,
            "zg" => ZGFontSize,
            _ => 8
        };

        protected override void OnCalculate(int bar, decimal value) { }

        private List<object> GetGroupedLevels()
        {
            var levels = new List<Nivel>(_niveles);
            levels.Sort((a, b) => a.Precio.CompareTo(b.Precio));
            var result = new List<object>();
            int idx = 0;

            while (idx < levels.Count)
            {
                var group = new List<Nivel> { levels[idx] };
                while (idx + 1 < levels.Count && Math.Abs(levels[idx + 1].Precio - levels[idx].Precio) <= GroupThreshold)
                {
                    group.Add(levels[++idx]);
                }

                if (group.Count > 1)
                {
                    decimal maxPrice = group[0].Precio;
                    decimal minPrice = group[0].Precio;
                    string label = group[0].Etiqueta;
                    Color color = group[0].Color;
                    int grosor = group[0].Grosor;
                    DashStyle estilo = group[0].Estilo;
                    int tamanoTexto = group[0].TamanoTexto;

                    for (int i = 1; i < group.Count; i++)
                    {
                        maxPrice = Math.Max(maxPrice, group[i].Precio);
                        minPrice = Math.Min(minPrice, group[i].Precio);
                        label += "+" + group[i].Etiqueta;
                    }

                    result.Add(new Zona
                    {
                        PrecioAlto = maxPrice,
                        PrecioBajo = minPrice,
                        Etiqueta = label,
                        Color = color,
                        Grosor = grosor,
                        Estilo = estilo,
                        TamanoTexto = tamanoTexto
                    });
                }
                else
                {
                    result.Add(group[0]);
                }

                idx++;
            }

            return result;
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
            {
                context.DrawString("Error: ChartInfo o PriceChartContainer no disponibles", new RenderFont("Arial", 10), Color.Red, 10, 10);
                return;
            }

            // Mostrar fecha del archivo cargado (si existe)
            if (_loaded && !string.IsNullOrEmpty(_fileDateText))
            {
                var dateFont = new RenderFont("Arial", 9);
                var dateText = $"SpotGamma fecha: {_fileDateText}";
                int textW = EstimateTextWidth(dateText, dateFont);
                int y = 10 + _dateLabelOffsetY;
                int x = 10;
                switch (_dateAlignment)
                {
                    case DateLabelAlignment.Center:
                        x = (ChartInfo.Region.Width - textW) / 2;
                        break;
                    case DateLabelAlignment.Right:
                        x = Math.Max(0, ChartInfo.Region.Width - textW - 10);
                        break;
                    case DateLabelAlignment.Left:
                    default:
                        x = 10;
                        break;
                }
                context.DrawString(dateText, dateFont, Color.Gray, x, y);
            }

            bool hasManual = HasAnyManualEnabled();

            if (!string.IsNullOrEmpty(_errorMessage) && !hasManual)
            {
                context.DrawString(_errorMessage, new RenderFont("Arial", 10), Color.Red, 10, 10);
                return;
            }

            if (!_loaded && !hasManual)
            {
                context.DrawString("No se cargaron niveles. Verifique el archivo o símbolo.", new RenderFont("Arial", 10), Color.Red, 10, 30);
                return;
            }

            var xPos = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);

            // Asegurar que _niveles refleja el estado actual (por si cambió algo en propiedades)
            // No recargamos archivo aquí para evitar IO en render; solo reconstruimos lista manual si procede.
            // UpdateIndicator ya se invoca en setters; aquí asumimos _niveles listo.

            var groupedLevels = GetGroupedLevels();

            foreach (var item in groupedLevels)
            {
                if (item is Zona zona)
                {
                    var yTop = ChartInfo.PriceChartContainer.GetYByPrice(zona.PrecioAlto, false);
                    var yBottom = ChartInfo.PriceChartContainer.GetYByPrice(zona.PrecioBajo, false);
                    var rect = new Rectangle(0, yTop, xPos, yBottom - yTop);
                    context.FillRectangle(Color.FromArgb(50, zona.Color), rect);

                    if (MostrarEtiquetas)
                    {
                        context.DrawString(zona.Etiqueta, new RenderFont("Arial", zona.TamanoTexto), zona.Color, xPos + LabelOffsetX, yTop - zona.TamanoTexto - 2);
                    }
                }
                else if (item is Nivel nivel)
                {
                    var yPos = ChartInfo.PriceChartContainer.GetYByPrice(nivel.Precio, false);
                    context.DrawLine(new RenderPen(nivel.Color, nivel.Grosor) { DashStyle = nivel.Estilo }, 0, yPos, xPos, yPos);

                    if (MostrarEtiquetas)
                    {
                        context.DrawString(nivel.Etiqueta, new RenderFont("Arial", nivel.TamanoTexto), nivel.Color, xPos + LabelOffsetX, yPos - nivel.TamanoTexto - 2);
                    }
                }
            }
        }

        #region Load Levels
        private void LoadLevels(string currentSymbol)
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                _errorMessage = $"Archivo no encontrado: {FilePath}";
                _loaded = false;
                _fileDateText = string.Empty;
                return;
            }

            try
            {
                string content = File.ReadAllText(FilePath);
                // Capturar fecha de encabezado (línea que empieza con '# YYYY-MM-DD ...')
                var dateMatch = Regex.Match(content, @"^#\s*(\d{4}-\d{2}-\d{2}[^\r\n]*)", RegexOptions.Multiline);
                _fileDateText = dateMatch.Success ? dateMatch.Groups[1].Value.Trim() : string.Empty;
                Regex regex = new Regex(@"def (\w+)_(\w+) = ([\d\.]+|Double\.NaN);");
                var allLevels = new Dictionary<string, Dictionary<string, decimal>>();

                foreach (Match match in regex.Matches(content))
                {
                    string sym = match.Groups[1].Value.ToUpper();
                    string key = match.Groups[2].Value.ToLower();
                    string valStr = match.Groups[3].Value;

                    if (valStr == "Double.NaN") continue;
                    if (!decimal.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal val)) continue;

                    if (!allLevels.ContainsKey(sym)) allLevels[sym] = new Dictionary<string, decimal>();
                    allLevels[sym][key] = val;
                }

                string symKey = GetSymKey(currentSymbol);
                if (symKey != null && allLevels.ContainsKey(symKey))
                {
                    _levels.Clear();
                    foreach (var kv in allLevels[symKey]) _levels[kv.Key] = kv.Value;
                    _loaded = true;
                    _errorMessage = string.Empty;
                }
                else
                {
                    _errorMessage = $"No se encontraron niveles para el símbolo: {currentSymbol}";
                    _loaded = false;
                    _fileDateText = string.Empty;
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Error al cargar niveles: {ex.Message}";
                _loaded = false;
                _fileDateText = string.Empty;
            }
        }

        private string GetSymKey(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;
            string[] known = { "SPX", "SPY", "QQQ", "NDX", "IWM", "RUT", "ES", "NQ", "RTY" };
            foreach (var k in known) if (symbol.Contains(k)) return k;
            return null;
        }

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double factor = 0.58; // heurística simple
            return (int)Math.Ceiling(text.Length * (font.Size * factor));
        }
        #endregion
        #endregion
    }
}