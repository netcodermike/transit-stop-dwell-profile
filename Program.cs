using System.Text.Json;

try
{
    if (args.Length != 1) throw new ArgumentException("Pass one JSON input file");
    using var data = JsonDocument.Parse(File.ReadAllText(args[0]));
    Console.WriteLine(JsonSerializer.Serialize(Analyze(data.RootElement)));
    return 0;
}
catch (Exception error) when (error is IOException or JsonException or FormatException
                               or ArgumentException or InvalidOperationException
                               or KeyNotFoundException or ArithmeticException or IndexOutOfRangeException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

static object Analyze(JsonElement input)
{
    var keys = new HashSet<(string Trip, int Sequence)>();
    var events = new List<(string Trip, int Sequence, string Stop, DateTimeOffset PlannedArrival,
        DateTimeOffset PlannedDeparture, DateTimeOffset ActualArrival, DateTimeOffset ActualDeparture)>();
    foreach (var row in input.GetProperty("events").EnumerateArray())
    {
        string trip = RequiredCode(row, "trip_id");
        string stop = RequiredCode(row, "stop_id");
        int sequence = row.GetProperty("stop_sequence").GetInt32();
        if (sequence < 1 || !keys.Add((trip, sequence)))
            throw new ArgumentException("Each trip must have unique positive stop sequence numbers.");

        DateTimeOffset plannedArrival = Timestamp(row, "planned_arrival");
        DateTimeOffset plannedDeparture = Timestamp(row, "planned_departure");
        DateTimeOffset actualArrival = Timestamp(row, "actual_arrival");
        DateTimeOffset actualDeparture = Timestamp(row, "actual_departure");
        if (plannedDeparture < plannedArrival || actualDeparture < actualArrival)
            throw new ArgumentException($"Trip {trip} departs before arriving at {stop}.");
        events.Add((trip, sequence, stop, plannedArrival, plannedDeparture, actualArrival, actualDeparture));
    }
    if (events.Count == 0) throw new ArgumentException("Supply at least one observed transit event.");

    var trips = new List<object>();
    foreach (var trip in events.GroupBy(item => item.Trip).OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        var stops = new List<object>();
        DateTimeOffset? previousPlannedDeparture = null, previousActualDeparture = null;
        decimal previousDepartureDelay = 0, initialDelay = 0, dwellChange = 0, betweenStopsChange = 0;
        foreach (var visit in trip.OrderBy(item => item.Sequence))
        {
            if (visit.PlannedArrival < previousPlannedDeparture || visit.ActualArrival < previousActualDeparture)
                throw new ArgumentException($"Trip {trip.Key} has overlapping stop visits at sequence {visit.Sequence}.");

            decimal arrivalDelay = Seconds(visit.ActualArrival - visit.PlannedArrival);
            decimal departureDelay = Seconds(visit.ActualDeparture - visit.PlannedDeparture);
            decimal? incomingChange = previousActualDeparture.HasValue ? arrivalDelay - previousDepartureDelay : null;
            decimal stopChange = departureDelay - arrivalDelay;
            if (stops.Count == 0) initialDelay = arrivalDelay;
            dwellChange += stopChange;
            betweenStopsChange += incomingChange ?? 0;
            stops.Add(new
            {
                stop_id = visit.Stop, stop_sequence = visit.Sequence,
                arrival_delay_seconds = arrivalDelay, departure_delay_seconds = departureDelay,
                dwell_change_seconds = stopChange, incoming_run_change_seconds = incomingChange,
            });
            previousPlannedDeparture = visit.PlannedDeparture;
            previousActualDeparture = visit.ActualDeparture;
            previousDepartureDelay = departureDelay;
        }
        trips.Add(new
        {
            trip_id = trip.Key, initial_delay_seconds = initialDelay, final_delay_seconds = previousDepartureDelay,
            dwell_change_seconds = dwellChange, between_stops_change_seconds = betweenStopsChange, stops,
        });
    }
    return new { event_count = events.Count, trips };
}

static decimal Seconds(TimeSpan duration) => duration.Ticks / (decimal)TimeSpan.TicksPerSecond;

static string RequiredCode(JsonElement row, string field)
{
    string? value = row.GetProperty(field).GetString()?.Trim();
    return string.IsNullOrEmpty(value) ? throw new ArgumentException($"Supply {field}.") : value;
}

static DateTimeOffset Timestamp(JsonElement row, string field)
{
    string timestamp = RequiredCode(row, field);
    if (timestamp.EndsWith('Z')) timestamp = timestamp[..^1] + "+00:00";
    if (!DateTimeOffset.TryParseExact(timestamp,
        new[] { "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" },
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out DateTimeOffset parsed))
        throw new ArgumentException($"{field} needs an ISO 8601 timestamp with Z or a numeric UTC offset.");
    return parsed;
}
