namespace SoundpadTrackHub.Models
{
    public enum SoundpadConnectionState
    {
        /// <summary>Soundpad ещё не обнаружен в этом сеансе.</summary>
        Waiting,

        /// <summary>Процесс Soundpad.exe найден, окно отслеживается.</summary>
        Connected,

        /// <summary>Soundpad был запущен, но закрылся.</summary>
        Closed,
    }

    public static class SoundpadConnectionStateExtensions
    {
        public static string DisplayText(this SoundpadConnectionState state) => state switch
        {
            SoundpadConnectionState.Connected => "Подключён",
            SoundpadConnectionState.Closed => "Soundpad закрыт",
            _ => "Ожидание Soundpad",
        };
    }
}
