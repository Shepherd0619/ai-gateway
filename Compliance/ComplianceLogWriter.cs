using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Compliance;

internal sealed class ComplianceLogWriter : BackgroundService
{
    private readonly ILogger<ComplianceLogWriter> _logger;
    private readonly ComplianceLogOptions _options;
    private readonly Channel<ComplianceEntry> _channel;

    public ComplianceLogWriter(ILogger<ComplianceLogWriter> logger, IOptions<ComplianceLogOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _channel = Channel.CreateBounded<ComplianceEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>
    /// Whether compliance logging is enabled. When disabled, <see cref="TryWrite"/>
    /// is a no-op so the bounded channel never accumulates full request/response
    /// bodies in memory (the background reader stops draining when disabled).
    /// </summary>
    public bool IsEnabled => _options.Enabled;

    public bool TryWrite(ComplianceEntry entry) =>
        _options.Enabled && _channel.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Compliance logging is disabled");
            return;
        }

        var dir = Path.GetDirectoryName(_options.Path);
        if (dir is not null) Directory.CreateDirectory(dir);

        await using var writer = new StreamWriter(_options.Path, append: true, Encoding.UTF8);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry));
                }
                await writer.FlushAsync(stoppingToken);
            }
        }
    }
}
