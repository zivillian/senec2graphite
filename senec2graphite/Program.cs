using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ahd.Graphite;
using Mono.Options;

string? senec = null;
string graphite = "localhost";
string prefix = "senec";
bool showHelp = false;

var options = new OptionSet
{
    {"s|senec=", "Senec ip or hostname", x => senec = x},
    {"g|graphite=", $"graphite ip or hostname - defaults to {graphite}", x => graphite = x},
    {"p|prefix=", $"graphite prefix - defaults to {prefix}", x => prefix = x.TrimEnd('.')},
    {"h|help", "show help", x => showHelp = x != null},
};
try
{
    if (options.Parse(args).Count > 0)
    {
        showHelp = true;
    }
}
catch (OptionException ex)
{
    Console.Error.Write("senec2graphite: ");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Try 'senec2graphite --help' for more information");
    return;
}
if (showHelp || senec is null)
{
    options.WriteOptionDescriptions(Console.Out);
    return;
}
using var cts = new CancellationTokenSource();
try
{
    Console.CancelKeyPress += (s, e) =>
    {
        cts.Cancel();
        e.Cancel = true;
    };
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };
    using var senecClient = new HttpClient(handler)
    {
        BaseAddress = new UriBuilder
        {
            Host = senec,
            Port = 443,
            Scheme = "https"
        }.Uri,
        
    };
    var response = await senecClient.PostAsync("lala.cgi",
        new StringContent("{\"DEBUG\":{\"SECTIONS\":\"\"},\"PLAIN\":{\"SECTIONS\":\"\"}}",
            Encoding.UTF8, "application/json"), cts.Token);
    response.EnsureSuccessStatusCode();
    var groups = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cts.Token);
    var sections = new List<string>();
    foreach (var group in groups)
    {
        if (group.Value.AsObject().TryGetPropertyValue("SECTIONS", out var s))
            foreach (var section in s.AsArray())
            {
                var name = section.GetValue<string>();
                if (name.StartsWith("st_")) name = name.Substring(3);
                sections.Add(name);
            }
    }
    while (!cts.IsCancellationRequested)
    {
        var now = DateTime.Now;
        var carbon = new GraphiteClient(graphite, new PlaintextGraphiteFormatter());
        foreach (var section in sections)
        {
            response = await senecClient.PostAsync("lala.cgi",
                new StringContent($"{{\"{section}\":{{}}}}", Encoding.UTF8, "application/json"), cts.Token);
            response.EnsureSuccessStatusCode();
            var values = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cts.Token);
            if (values.TryGetPropertyValue(section, out var data))
            {
                var datapoints = new List<Datapoint>();
                try
                {
                    foreach (var property in data.AsObject())
                    {
                        var name = $"{prefix}.{section}.{property.Key}".ToLowerInvariant();
                        if (property.Value is JsonArray)
                        {
                            var array = property.Value.AsArray();
                            for (var i = 0; i < array.Count; i++)
                            {
                                datapoints.Add(new Datapoint($"{name}.{i}",
                                    ParseSenec(array[i].GetValue<string>()),
                                    DateTime.Now));
                            }
                        }
                        else
                        {
                            var value = property.Value.GetValue<string>();
                            datapoints.Add(new Datapoint(name, ParseSenec(value), DateTime.Now));
                        }
                    }
                }
                catch (ArgumentException)
                {
                    //section SENEC_IO_OUTPUT contains DC_SWITCH twice - so this throws an exception for duplicate keys
                }
                datapoints = datapoints.Where(x => !Double.IsNaN(x.Value)).ToList();
                if (datapoints.Count > 0)
                {
                    await carbon.SendAsync(datapoints.Where(x => !Double.IsNaN(x.Value)).ToList(), cts.Token);
                }
            }
        }
        var delay = TimeSpan.FromSeconds(30) - (DateTime.Now - now);
        await Task.Delay(delay, cts.Token);
    }
}
catch (TaskCanceledException)
{
    //yep - that's what the user asked for
}

double ParseSenec(string value)
{
    if (value.StartsWith("st_"))
    {
        return Double.NaN;
    }
    else if (value.StartsWith("u8_"))
    {
        return Byte.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("u1_"))
    {
        return UInt16.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("u3_"))
    {
        return UInt32.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("u6_"))
    {
        return UInt64.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("i8_"))
    {
        return SByte.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("i1_"))
    {
        return Int16.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("i3_"))
    {
        return Int32.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("i6_"))
    {
        return Int64.Parse(value.Substring(3),NumberStyles.HexNumber);
    }
    else if (value.StartsWith("fl_"))
    {
        var integer = Int32.Parse(value.Substring(3),NumberStyles.HexNumber);
        var bytes = BitConverter.GetBytes(integer);
        return BitConverter.ToSingle(bytes);
    }
    else
    {
        throw new NotImplementedException();
    }

}
