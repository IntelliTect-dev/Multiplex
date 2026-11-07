namespace TripIt.UnitTests;

/// <summary>
/// Synthetic TripIt payloads. Shapes mirror the live API (every scalar a string; a
/// one-item collection is an object, not an array) but every value is invented.
/// </summary>
internal static class Fixtures
{
    public const string TripUuid = "11111111-aaaa-9000-0001-000000000001";
    public const string AirUuid = "22222222-bbbb-9000-0004-000000000002";
    public const string LodgingUuid = "33333333-cccc-9000-0004-000000000003";

    /// <summary>list/trip with a single trip (object, not array) and paging.</summary>
    public const string ListTripSingle = $$$"""
    {"timestamp":"1794000000","num_bytes":"500",
     "Trip":{"id":"100","uuid":"{{{TripUuid}}}","display_name":"Test Trip to Lisbon",
             "start_date":"2027-03-01","end_date":"2027-03-05","primary_location":"Lisbon, Portugal",
             "is_private":"false","is_pro_enabled":"true","relative_url":"/trip/show/uuid/{{{TripUuid}}}"},
     "Profile":{"@attributes":{"ref":"p1"},"public_display_name":"Test User"},
     "page_num":"1","page_size":"5","max_page":"1","total_items":"1"}
    """;

    public static string ListTripPage(int page, int maxPage, params string[] names)
    {
        var trips = string.Join(",", names.Select((n, i) =>
            $$$"""{"id":"{{{page * 100 + i}}}","uuid":"00000000-0000-9000-0001-{{{page:D6}}}{{{i:D6}}}","display_name":"{{{n}}}","start_date":"2020-0{{{page}}}-01","end_date":"2020-0{{{page}}}-02","primary_location":"Somewhere"}"""));
        return $$$"""{"timestamp":"1","Trip":[{{{trips}}}],"page_num":"{{{page}}}","page_size":"{{{names.Length}}}","max_page":"{{{maxPage}}}","total_items":"{{{maxPage * names.Length}}}"}""";
    }

    /// <summary>get/trip/…/include_objects/true: a two-leg flight (legs out of order), a hotel, weather.</summary>
    public const string TripWithObjects = $$$"""
    {"timestamp":"1794000000",
     "Trip":{"id":"100","uuid":"{{{TripUuid}}}","display_name":"Test Trip to Lisbon",
             "start_date":"2027-03-01","end_date":"2027-03-05","primary_location":"Lisbon, Portugal",
             "TripInvitees":{"Invitee":[
                {"@attributes":{"profile_ref":"p1"},"is_read_only":"false","is_traveler":"true","is_owner":"true"},
                {"@attributes":{"profile_ref":"p2"},"is_read_only":"true","is_traveler":"false"}]}},
     "AirObject":{"id":"200","uuid":"{{{AirUuid}}}","trip_uuid":"{{{TripUuid}}}","display_name":"Flight",
        "supplier_conf_num":"ABC123","booking_site_name":"Example Air","total_cost":"$500.00",
        "Segment":[
          {"uuid":"seg-2","StartDateTime":{"date":"2027-03-05","time":"10:00:00","timezone":"Europe/Lisbon","utc_offset":"+00:00"},
           "EndDateTime":{"date":"2027-03-05","time":"13:00:00","timezone":"America/New_York","utc_offset":"-05:00"},
           "start_airport_code":"LIS","start_city_name":"Lisbon","end_airport_code":"JFK","end_city_name":"New York",
           "marketing_airline":"Example Air","marketing_airline_code":"EX","marketing_flight_number":"2",
           "seats":"12A","Status":{"flight_status":"401"}},
          {"uuid":"seg-1","StartDateTime":{"date":"2027-03-01","time":"18:00:00","timezone":"America/New_York","utc_offset":"-05:00"},
           "EndDateTime":{"date":"2027-03-02","time":"06:30:00","timezone":"Europe/Lisbon","utc_offset":"+00:00"},
           "start_airport_code":"JFK","start_city_name":"New York","end_airport_code":"LIS","end_city_name":"Lisbon",
           "marketing_airline":"Example Air","marketing_airline_code":"EX","marketing_flight_number":"1",
           "seats":"","service_class":"Economy"}],
        "Traveler":{"first_name":"Test","last_name":"User"}},
     "LodgingObject":{"id":"300","uuid":"{{{LodgingUuid}}}","trip_uuid":"{{{TripUuid}}}","display_name":"Hotel",
        "supplier_name":"Hotel Example","supplier_conf_num":"H-9",
        "StartDateTime":{"date":"2027-03-02","time":"15:00:00","timezone":"Europe/Lisbon","utc_offset":"+00:00"},
        "EndDateTime":{"date":"2027-03-05","time":"11:00:00","timezone":"Europe/Lisbon","utc_offset":"+00:00"},
        "Address":{"address":"1 Example Street, Lisbon"},"room_type":"Double","relative_url":"/x"},
     "WeatherObject":[{"trip_uuid":"{{{TripUuid}}}","date":"2027-03-02","location":"Lisbon","avg_high_temp_c":"18"}],
     "Profile":[{"@attributes":{"ref":"p1"},"public_display_name":"Test User"},
                {"@attributes":{"ref":"p2"},"public_display_name":"Friend User"}]}
    """;

    public const string AkamaiBlockPage =
        "<HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY>You do not have permission. " +
        "Reference #18.abc https://errors.edgesuite.net/18.abc</BODY></HTML>";
}
