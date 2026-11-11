namespace SoundpadTrackHub.Models
{
    /// <summary>
    /// Режим громкости для предпрослушивания и для отдельной усиленной копии.
    /// Оригинальный скачанный файл никогда не изменяется.
    /// </summary>
    public enum VolumeMode
    {
        /// <summary>Оригинальная громкость файла.</summary>
        Normal = 0,

        /// <summary>Усиление примерно на +6 dB.</summary>
        Loud = 1,

        /// <summary>Усиление примерно на +12 dB.</summary>
        SuperLoud = 2,
    }

    public static class VolumeModeExtensions
    {
        public static double GainDb(this VolumeMode mode) => mode switch
        {
            VolumeMode.Loud => 6.0,
            VolumeMode.SuperLoud => 12.0,
            _ => 0.0,
        };

        public static string DisplayName(this VolumeMode mode) => mode switch
        {
            VolumeMode.Loud => "Громкая",
            VolumeMode.SuperLoud => "Супер громкая",
            _ => "Нормальная",
        };

        /// <summary>Суффикс имени файла для усиленной копии.</summary>
        public static string FileSuffix(this VolumeMode mode) => mode switch
        {
            VolumeMode.Loud => " (громкая +6 dB)",
            VolumeMode.SuperLoud => " (супер громкая +12 dB)",
            _ => string.Empty,
        };
    }
}
