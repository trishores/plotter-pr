namespace mine
{
    internal static class WeekdayTimeAxis
    {
        private static readonly DateTime BaseMonday = new DateTime(1970, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly long TicksPerDay = TimeSpan.FromDays(1).Ticks;
        private static readonly long TicksPerWeek = TimeSpan.FromDays(7).Ticks;

        public static bool IsWeekend(DateTimeOffset dto)
        {
            DayOfWeek dow = dto.ToLocalTime().DayOfWeek;
            return dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
        }

        public static double ToPlotTicks(DateTime localUnspecified)
        {
            long diff = localUnspecified.Ticks - BaseMonday.Ticks;
            long week = Math.DivRem(diff, TicksPerWeek, out long rem);
            if (rem < 0)
            {
                week--;
                rem += TicksPerWeek;
            }
            long maxWeekdayTicks = 5 * TicksPerDay;
            if (rem > maxWeekdayTicks) rem = maxWeekdayTicks;
            return (double)(week * maxWeekdayTicks + rem);
        }

        public static double ToPlotTicks(DateTimeOffset dto)
        {
            return ToPlotTicks(dto.ToLocalTime().DateTime);
        }

        public static DateTime ToLocalDateTime(double plotTicks)
        {
            long pt = (long)plotTicks;
            long maxWeekdayTicks = 5 * TicksPerDay;
            long week = Math.DivRem(pt, maxWeekdayTicks, out long rem);
            if (rem < 0)
            {
                week--;
                rem += maxWeekdayTicks;
            }
            long actualTicks = BaseMonday.Ticks + week * TicksPerWeek + rem;
            if (actualTicks < DateTime.MinValue.Ticks) return DateTime.MinValue;
            if (actualTicks > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
            return new DateTime(actualTicks, DateTimeKind.Local);
        }
    }
}