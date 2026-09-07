using System.Net;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LakeCountrySpanish.Web.Data;
using LakeCountrySpanish.Web.Models.Entities;
using LakeCountrySpanish.Web.Services;
using LakeCountrySpanish.Web.Services.Curriculum;
using LakeCountrySpanish.Web.Services.Curriculum.Blocks;
using LakeCountrySpanish.Web.Services.Media;
using Stripe;

var builder = WebApplication.CreateBuilder(args);

// Load appsettings.Local.json (gitignored) AFTER appsettings.{Environment}.json so
// that local secrets override committed placeholders without ever being tracked.
// This is the LCS convention — preferred over user-secrets because it doesn't
// require per-machine setup and is uniform with the appsettings.Production.json
// pattern used in deployment.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Configure logging to suppress noisy CookieTempDataProvider warnings
// These occur when a stale TempData cookie (encrypted with old keys) can't be decrypted
// This is expected behavior after app restarts in development - the cookie is simply ignored
builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.ViewFeatures.CookieTempDataProvider", LogLevel.Error);

// Configure Stripe
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// Add DbContext (PostgreSQL via Npgsql)
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// Configure cookie settings
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

// Reverse-proxy awareness. Caddy terminates TLS and forwards to
// localhost:5028/5029. Without ForwardedHeaders the app sees
// Request.Scheme="http", which breaks Stripe redirect URLs, cookie
// SecurePolicy, and logs the loopback IP instead of the real client.
// Registered first in the pipeline (below, before anything that reads
// scheme or IP).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
});

// Cookie policy: require Secure + HttpOnly + SameSite=Lax. Depends on
// ForwardedHeaders being active so Kestrel sees the request as HTTPS —
// otherwise Secure=Always would prevent cookies from being set.
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Lax;
    options.Secure = CookieSecurePolicy.Always;
    options.HttpOnly = HttpOnlyPolicy.Always;
});

// DataProtection keys: persist to disk so auth cookies + antiforgery
// tokens survive systemd restarts. Without this every deploy invalidates
// every session and logs everyone out. In development the default
// ephemeral store is fine — no config key set, no persistence, dev users
// re-login on restart. The keys directory itself is created by the
// deploy workflow at /var/lib/lakecountryspanish{-stg,}-data/keys with
// www-data ownership.
var dpKeysDir = builder.Configuration["DataProtection:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(dpKeysDir))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
        .SetApplicationName("LakeCountrySpanish");
}

// Add services
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IClassSchedulingService, ClassSchedulingService>();
builder.Services.AddScoped<IPaymentService, StripePaymentService>();
builder.Services.AddScoped<ISubscriptionService, StripeSubscriptionService>();
builder.Services.AddScoped<ITokenService, LakeCountrySpanish.Web.Services.TokenService>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddScoped<IGamificationService, GamificationService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<IClaudeApiService, ClaudeApiService>();
builder.Services.AddScoped<IAssignmentService, AssignmentService>();
builder.Services.AddScoped<IPlacementTestService, PlacementTestService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddSingleton<IDocumentRenderingService, DocumentRenderingService>();

// Enrollment Programs: unlisted /join/{slug} landing pages for open houses.
builder.Services.AddScoped<LakeCountrySpanish.Web.Services.Programs.IEnrollmentProgramService, LakeCountrySpanish.Web.Services.Programs.EnrollmentProgramService>();
builder.Services.AddScoped<LakeCountrySpanish.Web.Services.Programs.IProgramEnrollmentService, LakeCountrySpanish.Web.Services.Programs.ProgramEnrollmentService>();

// Media library: image processing primitives + storage orchestration + source adapters.
// PixabaySettings binds the gitignored appsettings.Local.json "Pixabay" section.
builder.Services.Configure<PixabaySettings>(builder.Configuration.GetSection(PixabaySettings.SectionName));
builder.Services.AddSingleton<IImageProcessingService, ImageProcessingService>();
builder.Services.AddScoped<IMediaService, MediaService>();
builder.Services.AddHttpClient<PixabayImageSourceAdapter>();
builder.Services.AddScoped<IImageSourceAdapter, PixabayImageSourceAdapter>();

// Curriculum authoring services.
builder.Services.AddScoped<ICurriculumDayService, CurriculumDayService>();
builder.Services.AddSingleton<IBlockCompiler, BlockCompiler>();
builder.Services.AddScoped<DocxLessonParser>();

builder.Services.AddScoped<INotificationScheduler, NotificationScheduler>();
builder.Services.AddHostedService<NotificationBackgroundService>();
builder.Services.AddHttpClient();

// Add MVC
builder.Services.AddControllersWithViews();

// Rate limiter — anti-abuse for the anonymous enrollment endpoint. Applied
// to POST /join/{slug} via [EnableRateLimiting("enrollment-submit")] on
// JoinController.Submit. Fixed 1-hour window per client IP; sized to
// comfortably cover a real family enrolling multiple kids without letting
// a script rip through hundreds of forged submissions (which happened
// 2026-09-06 — see git log for the incident-driven addition).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("enrollment-submit", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            }));
});

var app = builder.Build();

// MUST be first middleware — everything downstream (HttpsRedirection,
// cookies, logging) reads Request.Scheme + Connection.RemoteIpAddress
// and needs the forwarded values, not the loopback view.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCookiePolicy();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Health check for Caddy active checks + external monitoring. Returns
// 200 with JSON when the database is reachable, 503 otherwise. Kept
// dependency-free (no HealthChecks NuGet package) — a single CanConnect
// probe is sufficient for a small app.
app.MapGet("/healthz", async (ApplicationDbContext db, CancellationToken ct) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "healthy", database = "up" })
            : Results.Json(new { status = "unhealthy", database = "down" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", database = "error", message = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Apply pending database migrations automatically
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        if (pendingMigrations.Any())
        {
            logger.LogInformation("Applying {Count} pending database migration(s)...", pendingMigrations.Count());
            await context.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while applying database migrations.");
        throw; // Fail fast - don't start app with incompatible database
    }

    // Seed roles, admin user, and development test data
    await SeedData.InitializeAsync(services, app.Environment.IsDevelopment());
}

app.Run();
