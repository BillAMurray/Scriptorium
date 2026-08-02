using RAG.Services;

namespace RAG
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages();

            builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

            // A local model can take a long time to answer, so the default 100s timeout is far too short.
            builder.Services.AddHttpClient<OllamaClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>().Value;
                client.BaseAddress = new Uri(options.OllamaBaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

            // A named client rather than a typed one: SystemMetrics must be a singleton to hold
            // the previous CPU sample, and typed clients are registered transient.
            builder.Services.AddHttpClient(SystemMetrics.HttpClientName, (provider, client) =>
            {
                var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>().Value;
                client.BaseAddress = new Uri(options.OllamaBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(5);
            });

            builder.Services.AddSingleton<SystemMetrics>();
            builder.Services.AddSingleton<BrowserPresence>();
            builder.Services.AddHostedService<BrowserSessionService>();
            builder.Services.AddSingleton<DatasetRegistry>();
            builder.Services.AddSingleton<VectorStoreProvider>();
            builder.Services.AddSingleton<OcrService>();
            builder.Services.AddSingleton<DocumentTextExtractor>();
            builder.Services.AddSingleton<IndexingService>();
            builder.Services.AddScoped<RagService>();

            var app = builder.Build();

            // Close every dataset on the way out, which checkpoints each write-ahead log and
            // truncates it. A dataset folder left with a live journal beside its database is how
            // one index's journal comes to be mistaken for another's.
            app.Lifetime.ApplicationStopping.Register(
                () => app.Services.GetRequiredService<VectorStoreProvider>().ReleaseAll());

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.MapStaticAssets();
            app.MapRazorPages()
               .WithStaticAssets();

            app.Run();
        }
    }
}
