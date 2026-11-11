using System;
using System.Windows.Controls;

namespace SoundpadTrackHub.Views
{
    public partial class SoundpadVolumeSwitch : UserControl
    {
        public SoundpadVolumeSwitch()
        {
            InitializeComponent();
        }

        /// <summary>Уникальная группа, чтобы переключатели на разных страницах не мешали друг другу.</summary>
        public string GroupName { get; } = "SoundpadVolume-" + Guid.NewGuid().ToString("N");
    }
}
