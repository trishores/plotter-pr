using System;

namespace mine
{
    public enum TimeAxisMode
    {
        Compact,
        Expanded
    }

    internal static class WeekdayTimeAxis
    {
        private static readonly DateTime BaseMonday = new DateTime(1970, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly long TicksPerDay = TimeSpan.FromDays(1).Ticks;
        private static readonly long TicksPerWeek = TimeSpan.FromDays(7).Ticks;

        private static readonly long MarketOpenTicks = TimeSpan.FromHours(8).Ticks;
        private static readonly long MarketCloseTicks = TimeSpan.FromHours(16.5).Ticks;
        private static readonly long MarketDurationTicks = MarketCloseTicks - MarketOpenTicks; // 8.5 hours
        private static readonly long CompactGapTicks = TimeSpan.FromHours(4).Ticks; // 4 hours between translucent bands
        private static readonly long CompactDayStrideTicks = MarketDurationTicks + CompactGapTicks; // 12.5 hours per weekday
        private static readonly long CompactWeekStrideTicks = 5 * CompactDayStrideTicks;

        public static long GetPlotDayTicks(TimeAxisMode mode) => mode == TimeAxisMode.Expanded ? TicksPerDay : CompactDayStrideTicks;

        public static bool IsMarketOpen(DateTimeOffset dto)
        {
            DateTime local = dto.ToLocalTime().DateTime;
            if (local.DayOfWeek == DayOfWeek.Saturday || local.DayOfWeek == DayOfWeek.Sunday) return false;
            long timeOfDay = local.TimeOfDay.Ticks;
            return timeOfDay >= MarketOpenTicks && timeOfDay <= MarketCloseTicks;
        }

        public static double ToPlotTicks(DateTime localUnspecified, TimeAxisMode mode)
        {
            if (mode == TimeAxisMode.Expanded)
            {
                return (double)(localUnspecified.Ticks - BaseMonday.Ticks);
            }

            long diff = localUnspecified.Ticks - BaseMonday.Ticks;
            long week = Math.DivRem(diff, TicksPerWeek, out long rem);
            if (rem < 0)
            {
                week--;
                rem += TicksPerWeek;
            }

            long dayIndex = Math.DivRem(rem, TicksPerDay, out long timeOfDay);
            if (dayIndex >= 5)
            {
                return (double)(week * CompactWeekStrideTicks + 5 * CompactDayStrideTicks - CompactGapTicks / 2);
            }

            long dayStartPlot = week * CompactWeekStrideTicks + dayIndex * CompactDayStrideTicks;
            if (timeOfDay < MarketOpenTicks)
            {
                double ratio = (double)timeOfDay / MarketOpenTicks;
                return dayStartPlot - (CompactGapTicks * 0.5) * (1.0 - ratio);
            }
            if (timeOfDay <= MarketCloseTicks)
            {
                return dayStartPlot + (timeOfDay - MarketOpenTicks);
            }

            double afterRatio = (double)(timeOfDay - MarketCloseTicks) / (TicksPerDay - MarketCloseTicks);
            return dayStartPlot + MarketDurationTicks + (CompactGapTicks * 0.5) * afterRatio;
        }

        public static double ToPlotTicks(DateTimeOffset dto, TimeAxisMode mode)
        {
            return ToPlotTicks(dto.ToLocalTime().DateTime, mode);
        }

        public static DateTime ToLocalDateTime(double plotTicks, TimeAxisMode mode)
        {
            long pt = (long)plotTicks;
            if (mode == TimeAxisMode.Expanded)
            {
                long actualExpanded = BaseMonday.Ticks + pt;
                if (actualExpanded < DateTime.MinValue.Ticks) return DateTime.MinValue;
                if (actualExpanded > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
                return new DateTime(actualExpanded, DateTimeKind.Local);
            }

            long week = Math.DivRem(pt, CompactWeekStrideTicks, out long rem);
            if (rem < 0)
            {
                week--;
                rem += CompactWeekStrideTicks;
            }

            long dayIndex = Math.DivRem(rem, CompactDayStrideTicks, out long dayRem);
            if (dayIndex >= 5) dayIndex = 4;

            long timeOfDay;
            if (dayRem <= MarketDurationTicks)
            {
                timeOfDay = MarketOpenTicks + dayRem;
            }
            else
            {
                long gapOffset = dayRem - MarketDurationTicks;
                if (gapOffset <= CompactGapTicks / 2)
                {
                    double frac = (double)gapOffset / (CompactGapTicks * 0.5);
                    timeOfDay = MarketCloseTicks + (long)(frac * (TicksPerDay - MarketCloseTicks));
                }
                else
                {
                    dayIndex++;
                    double frac = (double)(gapOffset - CompactGapTicks * 0.5) / (CompactGapTicks * 0.5);
                    timeOfDay = (long)(frac * MarketOpenTicks);
                    if (dayIndex >= 5)
                    {
                        dayIndex = 0;
                        week++;
                    }
                }
            }

            long actualTicks = BaseMonday.Ticks + week * TicksPerWeek + dayIndex * TicksPerDay + timeOfDay;
            if (actualTicks < DateTime.MinValue.Ticks) return DateTime.MinValue;
            if (actualTicks > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
            return new DateTime(actualTicks, DateTimeKind.Local);
        }
    }
}