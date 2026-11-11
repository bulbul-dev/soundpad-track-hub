using System;

namespace SoundpadTrackHub.Integration
{
    /// <summary>Прямоугольник в физических пикселях экрана.</summary>
    public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
    }

    public enum DockSide
    {
        Right,
        Left,
        /// <summary>Ни справа, ни слева нет места — окно ставится у края экрана поверх Soundpad.</summary>
        Overlay,
    }

    public readonly record struct DockResult(PixelRect Bounds, DockSide Side);

    /// <summary>
    /// Чистый расчёт позиции Track Hub рядом с окном Soundpad.
    /// Справа, если помещается в рабочую область монитора; иначе слева; иначе у края.
    /// </summary>
    public static class DockPlacementCalculator
    {
        public static DockResult Calculate(PixelRect soundpad, PixelRect workArea, int width, int minHeight, int gap)
        {
            width = Math.Min(width, workArea.Width);
            var height = Math.Clamp(soundpad.Height, Math.Min(minHeight, workArea.Height), workArea.Height);
            var top = Math.Clamp(soundpad.Top, workArea.Top, workArea.Bottom - height);

            var rightLeft = soundpad.Right + gap;
            if (rightLeft + width <= workArea.Right)
            {
                return new DockResult(PixelRect.FromSize(rightLeft, top, width, height), DockSide.Right);
            }

            var leftLeft = soundpad.Left - gap - width;
            if (leftLeft >= workArea.Left)
            {
                return new DockResult(PixelRect.FromSize(leftLeft, top, width, height), DockSide.Left);
            }

            // Soundpad развёрнут или занимает почти весь экран: прижимаемся к правому краю.
            return new DockResult(PixelRect.FromSize(workArea.Right - width, top, width, height), DockSide.Overlay);
        }
    }
}
