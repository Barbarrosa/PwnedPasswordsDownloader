﻿using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

using HaveIBeenPwned.PwnedPasswords;

using Microsoft.Win32.SafeHandles;

using Polly;
using Polly.Extensions.Http;
using Polly.Retry;

using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<PwnedPasswordsDownloader>();

app.Configure(config => config.PropagateExceptions());

try
{
    return app.Run(args);
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return -99;
}

internal sealed class Statistics
{
    public int HashesDownloaded = 0;
    public int CloudflareRequests = 0;
    public int CloudflareHits = 0;
    public int CloudflareMisses = 0;
    public long CloudflareRequestTimeTotal = 0;
    public long ElapsedMilliseconds = 0;
    public double CloudflareHitPercentage => CloudflareHits / (double)CloudflareHits * 100;
    public double CloudflareMissPercentage => CloudflareHits / (double)CloudflareHits * 100;
    public double HashesPerSecond => HashesDownloaded / (ElapsedMilliseconds / 1000.0);
}

internal sealed class PwnedPasswordsDownloader : Command<PwnedPasswordsDownloader.Settings>
{
    internal int _hashesInProgress = 0;
    internal Statistics _statistics = new();
    internal static Encoding s_encoding = Encoding.UTF8;
    internal HttpClient _httpClient = InitializeHttpClient();
    internal AsyncRetryPolicy<HttpResponseMessage> _policy = HttpPolicyExtensions.HandleTransientHttpError().RetryAsync(10, OnRequestError);

    private static void OnRequestError(DelegateResult<HttpResponseMessage> arg1, int arg2)
    {
        string requestUri = arg1.Result?.RequestMessage?.RequestUri?.ToString() ?? "";
        if (arg1.Exception != null)
        {
            AnsiConsole.MarkupLine($"[yellow]Failed request #{arg2} while fetching {requestUri}. Exception message: {arg1.Exception.Message}.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Failed attempt #{arg2} fetching {requestUri}. Response contained HTTP Status code {arg1?.Result?.StatusCode}.[/]");
        }
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Name of the output. Defaults to pwnedpasswords, which writes the output to pwnedpasswords.txt for single file output, or a directory called pwnedpasswords.")]
        [CommandArgument(0, "[outputFile]")]
        public string OutputFile { get; init; } = "pwnedpasswords";

        [Description("The number of parallel requests to make to Have I Been Pwned to download the hash ranges. If omitted or less than 2, defaults to four times the number of processors on the machine.")]
        [CommandOption("-p||--parallelism")]
        [DefaultValue(0)]
        public int Parallelism { get; set; } = 0;

        [Description("When set, overwrite any existing files while writing the results. Defaults to false.")]
        [CommandOption("-o|--overwrite")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; } = false;

        [Description("When set, writes the hash ranges into a single .txt file. Otherwise downloads ranges to individual files into a subfolder. If ommited defaults to single file.")]
        [CommandOption("-s|--single")]
        [DefaultValue(true)]
        public bool SingleFile { get; set; } = true;

        [Description("When set, fetches NTLM hashes instead of SHA1.")]
        [CommandOption("-n|--ntlm")]
        [DefaultValue(false)]
        public bool FetchNtlm { get; set; } = false;
    }

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        if (settings.Parallelism < 2)
        {
            settings.Parallelism = Math.Max(Environment.ProcessorCount * 8, 2);
        }

        Task processingTask = AnsiConsole.Progress()
            .AutoRefresh(false) // Turn off auto refresh
            .AutoClear(false)   // Do not remove the task list when done
            .HideCompleted(false)   // Hide tasks as they are completed
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),    // Task description
                new ProgressBarColumn(),        // Progress bar
                new PercentageColumn(),         // Percentage
                new RemainingTimeColumn(),      // Remaining time
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                if (settings.SingleFile)
                {
                    if (File.Exists(settings.OutputFile))
                    {
                        if (!settings.Overwrite)
                        {
                            AnsiConsole.MarkupLine($"Output file {settings.OutputFile.EscapeMarkup()}.txt already exists. Use -o if you want to overwrite it.");
                            return;
                        }

                        File.Delete(settings.OutputFile);
                    }
                }
                else
                {
                    if (Directory.Exists(settings.OutputFile))
                    {
                        if (!settings.Overwrite && Directory.EnumerateFiles(settings.OutputFile).Any())
                        {
                            AnsiConsole.MarkupLine($"Output directory {settings.OutputFile.EscapeMarkup()} already exists and is not empty. Use -o if you want to overwrite files.");
                            return;
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(settings.OutputFile);
                    }
                }


                var timer = Stopwatch.StartNew();
                ProgressTask progressTask = ctx.AddTask("[green]Hash ranges downloaded[/]", true, 1024 * 1024);
                Task processTask = ProcessRanges(settings);

                do
                {
                    progressTask.Value = _statistics.HashesDownloaded;
                    ctx.Refresh();
                    await Task.Delay(100).ConfigureAwait(false);
                }
                while (!processTask.IsCompleted);

                if (processTask.Exception is not null && processTask.Exception.InnerException is not null)
                {
                    AnsiConsole.WriteException(processTask.Exception.InnerException);
                }

                _statistics.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                progressTask.Value = _statistics.HashesDownloaded;
                ctx.Refresh();
                progressTask.StopTask();
            });

        processingTask.Wait();
        AnsiConsole.MarkupLine($"Finished downloading all hash ranges in {_statistics.ElapsedMilliseconds:N0}ms ({_statistics.HashesPerSecond:N2} hashes per second).");
        AnsiConsole.MarkupLine($"We made {_statistics.CloudflareRequests:N0} Cloudflare requests (avg response time: {(double)_statistics.CloudflareRequestTimeTotal / _statistics.CloudflareRequests:N2}ms). Of those, Cloudflare had already cached {_statistics.CloudflareHits:N0} requests, and made {_statistics.CloudflareMisses:N0} requests to the Have I Been Pwned origin server.");

        return 0;
    }

    private static HttpClient InitializeHttpClient()
    {
        var handler = new HttpClientHandler();

        if (handler.SupportsAutomaticDecompression)
        {
            handler.AutomaticDecompression = DecompressionMethods.All;
            handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12;
        }

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pwnedpasswords.com/range/"), DefaultRequestVersion = HttpVersion.Version20 };
        string? process = Environment.ProcessPath;
        if (process != null)
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hibp-downloader", FileVersionInfo.GetVersionInfo(process).ProductVersion));
        }

        return client;
    }

    private async Task<Stream> GetPwnedPasswordsRangeFromWeb(int i, bool fetchNtlm)
    {
        var cloudflareTimer = Stopwatch.StartNew();
        string requestUri = GetHashRange(i);
        if (fetchNtlm)
        {
            requestUri += "?mode=ntlm";
        }

        HttpResponseMessage response = await _policy.ExecuteAsync(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }).ConfigureAwait(false);
        Stream content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        Interlocked.Add(ref _statistics.CloudflareRequestTimeTotal, cloudflareTimer.ElapsedMilliseconds);
        Interlocked.Increment(ref _statistics.CloudflareRequests);
        if (response.Headers.TryGetValues("CF-Cache-Status", out IEnumerable<string>? values) && values != null)
        {
            switch (values.FirstOrDefault())
            {
                case "HIT":
                    Interlocked.Increment(ref _statistics.CloudflareHits);
                    break;
                default:
                    Interlocked.Increment(ref _statistics.CloudflareMisses);
                    break;
            };
        }

        return content;
    }

    private string GetHashRange(int i)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, i);
        return Convert.ToHexString(bytes)[3..];
    }

    private async Task ProcessRanges(Settings settings)
    {
        if (settings.SingleFile)
        {
            Channel<Task<Stream>> downloadTasks = Channel.CreateBounded<Task<Stream>>(new BoundedChannelOptions(settings.Parallelism) { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = true });
            using FileStream file = File.Open($"{settings.OutputFile}.txt", new FileStreamOptions { Access = FileAccess.Write, BufferSize = 32767, Mode = FileMode.Create, Options = FileOptions.Asynchronous, Share = FileShare.None });
            using StreamWriter writer = new(file);
            Task producerTask = StartDownloads(downloadTasks.Writer, settings.FetchNtlm);
            await foreach (Task<Stream> item in downloadTasks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                string prefix = GetHashRange(_statistics.HashesDownloaded++);
                using Stream inputStream = await item.ConfigureAwait(false);
                using StreamReader reader = new StreamReader(inputStream);
                string? line = null;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.Length > 0)
                    {
                        await writer.WriteLineAsync($"{prefix}{line}");
                    }
                }

                await writer.FlushAsync();
            }

            await producerTask.ConfigureAwait(false);
        }
        else
        {
            Task[] downloadTasks = new Task[settings.Parallelism];
            for (int i = 0; i < downloadTasks.Length; i++)
            {
                downloadTasks[i] = DownloadRangeToFile(settings.OutputFile, settings.FetchNtlm);
            }

            await Task.WhenAll(downloadTasks).ConfigureAwait(false);
        }
    }

    private async Task StartDownloads(ChannelWriter<Task<Stream>> channelWriter, bool fetchNtlm)
    {
        try
        {
            for (int i = 0; i < 1024 * 1024; i++)
            {
                await channelWriter.WriteAsync(GetPwnedPasswordsRangeFromWeb(i, fetchNtlm));
            }

            channelWriter.TryComplete();
        }
        catch (Exception e)
        {
            channelWriter.TryComplete(e);
        }
    }

    private async Task DownloadRangeToFile(string outputDirectory, bool fetchNtlm)
    {
        int nextHash = Interlocked.Increment(ref _hashesInProgress);
        int currentHash = nextHash - 1;
        while (currentHash < 1024 * 1024)
        {
            using Stream stream = await GetPwnedPasswordsRangeFromWeb(currentHash, fetchNtlm).ConfigureAwait(false);
            using SafeFileHandle handle = File.OpenHandle(Path.Combine(outputDirectory, $"{GetHashRange(currentHash)}.txt"), FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
            await handle.CopyFrom(stream).ConfigureAwait(false);
            Interlocked.Increment(ref _statistics.HashesDownloaded);
            nextHash = Interlocked.Increment(ref _hashesInProgress);
            currentHash = nextHash - 1;
        }
    }
}
