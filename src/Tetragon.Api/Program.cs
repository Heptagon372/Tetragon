using Microsoft.EntityFrameworkCore;
using Tetragon.Api.Endpoints;
using Tetragon.Infrastructure;
using Tetragon.Infrastructure.Persistence;
using Tetragon.Plugin.Abstractions;
using Tetragon.Plugins.Ai;
using Tetragon.Plugins.Markets;
using Tetragon.Plugins.Suppliers;

var builder = WebApplication.CreateBuilder(args);

// ── 저장 위치: 실행 폴더의 tetragon.db ──
var dbPath = Path.Combine(AppContext.BaseDirectory, "tetragon.db");
builder.Services.AddTetragonCore($"Data Source={dbPath}");

// ── 플러그인 등록 (설계서 ADR-005: 새 플러그인 = 여기에 한 줄 추가) ──
// 외부 API는 대부분 gzip으로 응답한다. 자동 압축 해제를 빠뜨리면
// 압축된 바이트를 그대로 파싱하게 되어 "'0x1F' is an invalid start of a value"가 난다.
// 모든 아웃바운드 클라이언트에 동일하게 적용한다.
static IHttpClientBuilder WithDecompression(IHttpClientBuilder builder) =>
    builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false, // 쿠키는 헤더로 직접 주입
    });

WithDecompression(builder.Services.AddHttpClient("scraper", c => c.Timeout = TimeSpan.FromSeconds(30)));
WithDecompression(builder.Services.AddHttpClient("smartstore", c => c.Timeout = TimeSpan.FromSeconds(30)));
WithDecompression(builder.Services.AddHttpClient("coupang", c => c.Timeout = TimeSpan.FromSeconds(60)));
WithDecompression(builder.Services.AddHttpClient("claude", c => c.Timeout = TimeSpan.FromMinutes(3)));
WithDecompression(builder.Services.AddHttpClient("11st", c => c.Timeout = TimeSpan.FromSeconds(60)));
// fetch 사이드카(Playwright)는 브라우저 렌더+행동 시뮬레이션 때문에 응답이 느리다. 타임아웃을 넉넉히.
WithDecompression(builder.Services.AddHttpClient("fetch-sidecar", c => c.Timeout = TimeSpan.FromSeconds(90)));

// 공급처
builder.Services.AddSingleton<ISupplierPlugin, AliExpressSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin, TaobaoSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin, AmazonSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin, ElevenStSupplierPlugin>();
// 쿠팡(Akamai 보호) — 브라우저 fetch 사이드카 경유로 수집. 상세+검색(카테고리 크롤러) 둘 다.
builder.Services.AddSingleton<CoupangSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin>(sp => sp.GetRequiredService<CoupangSupplierPlugin>());
builder.Services.AddSingleton<ICategoryCrawler>(sp => sp.GetRequiredService<CoupangSupplierPlugin>());
// 도매꾹·시뮬레이션은 카테고리 크롤링도 지원 — 같은 인스턴스를 두 인터페이스로 노출한다
builder.Services.AddSingleton<DomeggookSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin>(sp => sp.GetRequiredService<DomeggookSupplierPlugin>());
builder.Services.AddSingleton<ICategoryCrawler>(sp => sp.GetRequiredService<DomeggookSupplierPlugin>());
// 발주 플러그인 (자동 구매)
builder.Services.AddSingleton<ISupplierOrderPlugin, DomeggookOrderPlugin>();
builder.Services.AddSingleton<SimulatedSupplierPlugin>();
builder.Services.AddSingleton<ISupplierPlugin>(sp => sp.GetRequiredService<SimulatedSupplierPlugin>());
builder.Services.AddSingleton<ICategoryCrawler>(sp => sp.GetRequiredService<SimulatedSupplierPlugin>());

// 마켓 (API 등록)
builder.Services.AddSingleton<IMarketplaceAdapter, SmartStoreAdapter>();
// 쿠팡 호출 조절기 — 대량등록 시 스로틀링으로 빈 응답이 오는 것을 막는다
builder.Services.AddSingleton<CoupangThrottle>();
builder.Services.AddSingleton<IMarketplaceAdapter, CoupangAdapter>();
builder.Services.AddSingleton<IMarketplaceAdapter, ElevenStAdapter>();
// 옥션·G마켓은 공개 등록 API가 없어 엑셀 경로로 안내한다
builder.Services.AddSingleton<IMarketplaceAdapter, AuctionAdapter>();
builder.Services.AddSingleton<IMarketplaceAdapter, GmarketAdapter>();
builder.Services.AddSingleton<IMarketplaceAdapter, SimulatedMarketplaceAdapter>();

// 마켓 대량등록 엑셀 양식
builder.Services.AddSingleton<IMarketExcelTemplate, EsmPlusExcelTemplate>();
builder.Services.AddSingleton<IMarketExcelTemplate, ElevenStExcelTemplate>();
builder.Services.AddSingleton<IMarketExcelTemplate, CoupangExcelTemplate>();
builder.Services.AddSingleton<IMarketExcelTemplate, SmartStoreExcelTemplate>();

builder.Services.AddSingleton<IAiProviderPlugin, ClaudeAiPlugin>();
builder.Services.AddSingleton<IAiProviderPlugin, SimulatedAiPlugin>();

// 주문 이행 자동화 워커 — 정책이 켜져 있을 때만 실제로 동작한다
builder.Services.AddHostedService<Tetragon.Api.FulfillmentWorker>();
// 주의 원장 스윕 — 사라진 사실을 자동으로 닫는다 (확장 08 §2 attention.sweep)
builder.Services.AddHostedService<Tetragon.Api.AttentionSweepWorker>();

// ── 웹 ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new()
{
    Title = "Tetragon API",
    Version = "v1",
    Description = "구매대행·위탁판매 자동화 플랫폼 — 대량 수집/등록 파이프라인",
}));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://localhost:4173")
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    // enum을 숫자로 내보내면 화면에서 0/1/2가 보인다. 이름 그대로 보낸다.
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// ── 초기화: 스키마 생성 + 기본 정책/금지어 시드 ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TetragonDbContext>();
    await DbSeeder.InitializeAsync(db);
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tetragon API v1"));

// ── 엔드포인트 (설계서 §8) ──
app.MapAttentionEndpoints();
app.MapImportEndpoints();
app.MapProductEndpoints();
app.MapJobEndpoints();
app.MapCategoryEndpoints();
app.MapExcelEndpoints();
app.MapWalletEndpoints();
app.MapShippingPlaceEndpoints();
app.MapAuditEndpoints();
app.MapPricingEndpoints();
app.MapListingEndpoints();
app.MapComplianceEndpoints();
app.MapOrderEndpoints();
app.MapCsEndpoints();
app.MapDashboardEndpoints();
app.MapPluginEndpoints();
app.MapSettingsEndpoints();
app.MapStreamEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
