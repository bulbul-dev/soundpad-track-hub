using System;
using System.Windows;
using Serilog;
using Forms = System.Windows.Forms;

namespace SoundpadTrackHub.Views
{
    /// <summary>Значок в области уведомлений: доступ к Track Hub, когда окно скрыто.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly System.Drawing.Icon? _drawingIcon;

        public TrayIcon(Action show, Action openHitmoz, Action openFolder, Action exit)
        {
            try
            {
                var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
                if (stream is not null)
                {
                    using (stream)
                    {
                        _drawingIcon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray icon resource failed");
            }

            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x28),
                ForeColor = System.Drawing.Color.FromArgb(0xE7, 0xEA, 0xF0),
                Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable()),
            };
            menu.Items.Add("Показать Track Hub", null, (_, _) => show());
            menu.Items.Add("Открыть Hitmoz", null, (_, _) => openHitmoz());
            menu.Items.Add("Открыть папку загрузок", null, (_, _) => openFolder());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => exit());

            _icon = new Forms.NotifyIcon
            {
                Icon = _drawingIcon ?? System.Drawing.SystemIcons.Application,
                Text = "Soundpad Track Hub",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) show();
            };
        }

        /// <summary>Всплывающая подсказка у значка в области уведомлений.</summary>
        public void ShowHint(string title, string text)
        {
            try
            {
                _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray balloon failed");
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _drawingIcon?.Dispose();
        }

        private sealed class DarkColorTable : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x28);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x2F, 0x34, 0x3E);
            private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x34, 0x3A, 0x45);

            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Border;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color SeparatorDark => Border;
            public override System.Drawing.Color SeparatorLight => Border;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
        }
    }
}
