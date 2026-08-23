using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Events;
using Tetragon.Domain.Sourcing;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Pipeline;

// ─────────────────────────────────────────────────────────────────────────
// 메인 파이프라인 (설계서 6.1)
// scrape.requested → 수집+표준화 → 번역 → 가격 → 컴플라이언스 → (등록 요청 시) 마켓 등록
// 각 핸들러는 자기 단계만 처리하고 다음 이벤트를 발행한다. 컨텍스트 간 DB 직접 참조 금지.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>1단계: 수집(스크래핑) + 표준화. 실패 시 지수 백오프 재시도 3회 → DLQ (설계서 5.1).</summary>
public sealed class ScrapeRequestedHandler(
    ISupplierPluginRegistry suppliers,
    IProductRepository products,
    IRawProductRepository rawProducts,
    IScrapeJobRepository jobs,
    ICredentialStore credentials,
    ProductNormalizer normalizer,
    JobProgress progress,
    IEventBus bus,
    ILogger<ScrapeRequestedHandler> logger) : IIntegrationEventHandler<ScrapeRequested>
{
    public async Task HandleAsync(ScrapeRequested @event, CancellationToken ct)
    {
        var job = await progress.AdvanceAsync(@event.JobId, JobStage.Collecting, JobState.Running, @event.Url, ct);
        if (job is null) return;

        try
        {
            var url = new Uri(@event.Url);
            var plugin = suppliers.Resolve(url)
                ?? throw new InvalidOperationException($"이 URL을 처리할 공급처 플러그인이 없습니다: {url.Host}");

            var supplierCredential = await credentials.GetAsync($"supplier:{plugin.Code}", ct);
            var context = new ScrapeContext
            {
                TenantId = @event.TenantId,
                CookieHeader = supplierCredential.Get("cookie"),
            };

            var raw = await plugin.CollectAsync(url, context, ct);

            // 표준화 (Draft → Normalized)
            await progress.AdvanceAsync(@event.JobId, JobStage.Normalizing, JobState.Running, null, ct);
            var product = normalizer.Normalize(raw, @event.TenantId, @event.JobId);
            product.TransitionTo(ProductStatus.Normalized);
            await products.AddAsync(product, ct);
            await products.SaveAsync(ct);

            // 원본 보존 (raw_products 대응)
            await rawProducts.AddAsync(RawProductRecord.Create(
                @event.TenantId, raw.SupplierCode, raw.SourceProductId, raw.Url, raw.RawJson, product.Id), ct);

            job = await jobs.FindAsync(@event.JobId, ct);
            job!.AttachProduct(product.Id, plugin.Code);
            await jobs.SaveAsync(ct);

            await bus.PublishAsync(new ProductCollected
            {
                JobId = @event.JobId,
                ProductId = product.Id,
                SupplierCode = plugin.Code,
                TenantId = @event.TenantId,
                CorrelationId = @event.JobId,
                CausationId = @event.EventId,
            }, ct);
            await bus.PublishAsync(new ProductNormalized
            {
                JobId = @event.JobId,
                ProductId = product.Id,
                TenantId = @event.TenantId,
                CorrelationId = @event.JobId,
                CausationId = @event.EventId,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "수집 실패: {Url} (Job {JobId})", @event.Url, @event.JobId);

            // 영구 실패는 재시도하지 않는다 (삭제된 상품을 3번 더 긁어봐야 결과는 같다)
            var isPermanent = ex is PermanentScrapeException;
            await progress.FailAsync(@event.JobId, ex.Message, ct, isPermanent);

            var failedJob = await jobs.FindAsync(@event.JobId, ct);
            if (!isPermanent && failedJob is not null && failedJob.CanRetry)
            {
                // 지수 백오프 재발행 (2^attempt 초)
                var delay = TimeSpan.FromSeconds(Math.Pow(2, failedJob.Attempts));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay, CancellationToken.None);
                    await bus.PublishAsync(@event with { EventId = Guid.NewGuid() }, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                await bus.PublishAsync(new PipelineFailed
                {
                    JobId = @event.JobId,
                    Stage = JobStage.Collecting.ToString(),
                    Error = ex.Message,
                    DeadLettered = true,
                    TenantId = @event.TenantId,
                    CorrelationId = @event.JobId,
                }, ct);
            }
        }
    }
}

/// <summary>2단계: Enrichment — AI 번역/옵션 정제/SEO 상품명 (설계서 5.3).</summary>
public sealed class EnrichmentHandler(
    IAiRouter aiRouter,
    IProductRepository products,
    JobProgress progress,
    IEventBus bus,
    ILogger<EnrichmentHandler> logger) : IIntegrationEventHandler<ProductNormalized>
{
    public async Task HandleAsync(ProductNormalized @event, CancellationToken ct)
    {
        await progress.AdvanceAsync(@event.JobId, JobStage.Enriching, JobState.Running, null, ct);
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        try
        {
            var sourceLocale = product.Name.Values.Keys.FirstOrDefault(k => k != "ko-KR") ?? "zh-CN";
            var sourceName = product.Name.GetOrFirst(sourceLocale);
            var sourceDescription = product.Description.GetOrFirst(sourceLocale);

            // 번역 배치: [상품명, 상세, 옵션그룹명..., 옵션값명...]
            var groupNames = product.OptionGroups.Select(g => g.Name).Distinct().ToList();
            var valueNames = product.OptionGroups.SelectMany(g => g.Values.Select(v => v.Name)).Distinct().ToList();
            var texts = new List<string> { sourceName, sourceDescription };
            texts.AddRange(groupNames);
            texts.AddRange(valueNames);

            var translator = aiRouter.Route(AiCapability.Translate, @event.TenantId);
            var result = await translator.ExecuteAsync(new AiRequest
            {
                Capability = AiCapability.Translate,
                SourceLocale = sourceLocale,
                TargetLocale = "ko-KR",
                Texts = texts,
                Context = string.Join(" > ", product.SourceCategory),
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException($"번역 실패: {result.Error}");

            var translated = result.Texts;
            product.SetTranslatedName("ko-KR", translated.ElementAtOrDefault(0) ?? sourceName);
            product.SetTranslatedDescription("ko-KR", translated.ElementAtOrDefault(1) ?? sourceDescription);

            var groupMap = new Dictionary<string, string>();
            var valueMap = new Dictionary<string, string>();
            for (var i = 0; i < groupNames.Count; i++)
                groupMap[groupNames[i]] = translated.ElementAtOrDefault(2 + i) ?? groupNames[i];
            for (var i = 0; i < valueNames.Count; i++)
                valueMap[valueNames[i]] = translated.ElementAtOrDefault(2 + groupNames.Count + i) ?? valueNames[i];
            product.RenameOptions(groupMap, valueMap);

            // SEO 상품명 — 지원 Provider가 있을 때만. 실패해도 파이프라인은 계속한다.
            try
            {
                var seoProvider = aiRouter.Route(AiCapability.ProductNameSeo, @event.TenantId);
                var seo = await seoProvider.ExecuteAsync(new AiRequest
                {
                    Capability = AiCapability.ProductNameSeo,
                    SourceLocale = "ko-KR",
                    TargetLocale = "ko-KR",
                    Texts = [product.Name.GetOrFirst("ko-KR")],
                    Context = string.Join(" > ", product.SourceCategory),
                }, ct);
                if (seo.Success && seo.Texts.Count > 0 && !string.IsNullOrWhiteSpace(seo.Texts[0]))
                    product.SetTranslatedName("ko-KR", seo.Texts[0]);
            }
            catch (InvalidOperationException)
            {
                logger.LogDebug("SEO 상품명 생성을 지원하는 Provider 없음 — 번역 결과 유지");
            }

            product.TransitionTo(ProductStatus.Enriched);
            await products.SaveAsync(ct);

            await bus.PublishAsync(new ProductEnriched
            {
                JobId = @event.JobId,
                ProductId = product.Id,
                TenantId = @event.TenantId,
                CorrelationId = @event.JobId,
                CausationId = @event.EventId,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Enrichment 실패 (Product {ProductId})", @event.ProductId);
            await FailPipelineAsync(products, progress, bus, @event.JobId, product, JobStage.Enriching, ex.Message, @event.TenantId, ct);
        }
    }

    internal static async Task FailPipelineAsync(
        IProductRepository products, JobProgress progress, IEventBus bus,
        Guid jobId, Product? product, JobStage stage, string error, string tenantId, CancellationToken ct)
    {
        if (product is not null)
        {
            try { product.TransitionTo(ProductStatus.Failed); await products.SaveAsync(ct); }
            catch { /* 이미 실패 상태 등 — 무시 */ }
        }
        await progress.FailAsync(jobId, error, ct);
        await bus.PublishAsync(new PipelineFailed
        {
            JobId = jobId,
            ProductId = product?.Id,
            Stage = stage.ToString(),
            Error = error,
            DeadLettered = true,
            TenantId = tenantId,
            CorrelationId = jobId,
        }, ct);
    }
}

/// <summary>3단계: 가격 계산 (설계서 5.4 Rule Engine).</summary>
public sealed class PricingHandler(
    PricingEngine engine,
    IProductRepository products,
    IPricingPolicyRepository policies,
    IScrapeJobRepository jobs,
    JobProgress progress,
    IEventBus bus,
    ILogger<PricingHandler> logger) : IIntegrationEventHandler<ProductEnriched>
{
    public async Task HandleAsync(ProductEnriched @event, CancellationToken ct)
    {
        await progress.AdvanceAsync(@event.JobId, JobStage.Pricing, JobState.Running, null, ct);
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        try
        {
            var job = await jobs.FindAsync(@event.JobId, ct);
            var policy = (job?.PricingPolicyId is Guid policyId ? await policies.FindAsync(policyId, ct) : null)
                ?? await policies.FindDefaultAsync(ct)
                ?? throw new InvalidOperationException("가격 정책이 없습니다. 기본 정책을 먼저 생성하세요.");

            var calculations = await engine.CalculateAsync(product, policy, ct);
            product.ApplyCalculatedPrices(calculations.ToDictionary(c => c.VariantId, c => c.FinalPrice));
            product.TransitionTo(ProductStatus.Priced);
            await products.SaveAsync(ct);
            await policies.AddCalculationsAsync(calculations, ct);
            await policies.SaveAsync(ct);

            await bus.PublishAsync(new PriceCalculated
            {
                JobId = @event.JobId,
                ProductId = product.Id,
                PolicyId = policy.Id,
                TenantId = @event.TenantId,
                CorrelationId = @event.JobId,
                CausationId = @event.EventId,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "가격 계산 실패 (Product {ProductId})", @event.ProductId);
            await EnrichmentHandler.FailPipelineAsync(products, progress, bus, @event.JobId, product, JobStage.Pricing, ex.Message, @event.TenantId, ct);
        }
    }
}

/// <summary>4단계: 컴플라이언스 — Pass/Warn → Ready, Block → 사용자 확인 큐 (설계서 5.5).</summary>
public sealed class ComplianceHandler(
    ComplianceEngine engine,
    IProductRepository products,
    JobProgress progress,
    IEventBus bus,
    ILogger<ComplianceHandler> logger) : IIntegrationEventHandler<PriceCalculated>
{
    public async Task HandleAsync(PriceCalculated @event, CancellationToken ct)
    {
        await progress.AdvanceAsync(@event.JobId, JobStage.Compliance, JobState.Running, null, ct);
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        try
        {
            var result = await engine.CheckAsync(product, ct);

            if (result.Verdict == ComplianceVerdict.Block)
            {
                product.TransitionTo(ProductStatus.Blocked);
                await products.SaveAsync(ct);
                var blockedWords = string.Join(", ", result.Hits.Where(h => h.Severity == ComplianceSeverity.Block).Select(h => h.Keyword));
                var job = await progress.AdvanceAsync(@event.JobId, JobStage.Compliance, JobState.Blocked, $"금지어 차단: {blockedWords}", ct);
            }
            else
            {
                product.TransitionTo(ProductStatus.Ready);
                await products.SaveAsync(ct);
                var job = await progress.CompleteAsync(@event.JobId,
                    result.Verdict == ComplianceVerdict.Warn ? "경고 있음 — 확인 권장" : "등록 준비 완료", ct);

                // 빠른 등록: 링크 하나로 마켓까지 보내는 흐름.
                // 사용자가 상품 화면에 들러 등록 버튼을 누르는 단계를 생략한다.
                // 차단(Block)된 상품은 여기 오지 않으므로, 금지어 검사를 건너뛰지 않는다.
                if (job is { HasAutoList: true })
                {
                    await progress.AdvanceAsync(@event.JobId, JobStage.Listing, JobState.Running,
                        $"{string.Join(", ", job.AutoListMarkets)} 등록 요청", ct);
                    await bus.PublishAsync(new ListingRequested
                    {
                        JobId = @event.JobId,
                        ProductId = product.Id,
                        MarketCodes = job.AutoListMarkets.ToList(),
                        TenantId = @event.TenantId,
                        CorrelationId = @event.JobId,
                        CausationId = @event.EventId,
                    }, ct);
                }
            }

            await bus.PublishAsync(new ComplianceChecked
            {
                JobId = @event.JobId,
                ProductId = product.Id,
                Verdict = result.Verdict.ToString(),
                TenantId = @event.TenantId,
                CorrelationId = @event.JobId,
                CausationId = @event.EventId,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "컴플라이언스 검사 실패 (Product {ProductId})", @event.ProductId);
            await EnrichmentHandler.FailPipelineAsync(products, progress, bus, @event.JobId, product, JobStage.Compliance, ex.Message, @event.TenantId, ct);
        }
    }
}

/// <summary>5단계: 마켓 등록 (listing.requested — 사용자 트리거, 설계서 5.6).</summary>
public sealed class ListingRequestedHandler(
    IMarketplaceAdapterRegistry markets,
    IProductRepository products,
    IListingRepository listings,
    ICredentialStore credentials,
    ListingPayloadBuilder payloadBuilder,
    ShippingPlaceResolver shippingPlaces,
    IPipelineNotifier notifier,
    JobProgress progress,
    IEventBus bus,
    ILogger<ListingRequestedHandler> logger) : IIntegrationEventHandler<ListingRequested>
{
    public async Task HandleAsync(ListingRequested @event, CancellationToken ct)
    {
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        ListingPayload payload;
        try
        {
            payload = payloadBuilder.Build(product);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Payload 생성 실패 (Product {ProductId})", @event.ProductId);
            await FinishJobAsync(@event.JobId, false, $"등록 정보 생성 실패: {ex.Message}", ct);
            return;
        }

        var anySuccess = false;
        var failures = new List<string>();
        foreach (var marketCode in @event.MarketCodes)
        {
            var listing = await listings.FindByProductAndMarketAsync(product.Id, marketCode, ct);
            if (listing is null)
            {
                listing = Domain.Listings.Listing.Create(@event.TenantId, product.Id, marketCode);
                await listings.AddAsync(listing, ct);
            }

            try
            {
                var adapter = markets.Resolve(marketCode)
                    ?? throw new InvalidOperationException($"마켓 어댑터 없음: {marketCode}");
                var credential = await credentials.GetAsync($"market:{marketCode}", ct);

                // 위탁판매: 공급처 주소를 마켓 출고지/반품지로 등록하고 코드를 받아 쓴다.
                var resolution = await shippingPlaces.ResolveAsync(
                    adapter, payload.Logistics, product.Source.SupplierCode, credential, ct);

                // 코드를 못 얻었으면 여기서 멈춘다.
                // 그대로 보내면 쿠팡이 "반품지센터코드를 입력하세요"만 돌려주고
                // 정작 무엇을 해야 하는지(어느 주소를 WING에 등록할지)는 사라진다.
                if (!resolution.CanProceed)
                {
                    var reason = string.Join(" ", resolution.Problems);
                    logger.LogWarning("{Market} 등록 중단 — 배송지 미확보 (Product {ProductId}): {Reason}",
                        marketCode, product.Id, reason);
                    listing.MarkFailed($"SHIPPING_PLACE: {reason}");
                    await listings.SaveAsync(ct);
                    failures.Add($"{marketCode}: {reason}");
                    notifier.Notify(new PipelineNotification(
                        @event.JobId ?? Guid.Empty, "Listing", "Failed", product.Id,
                        $"{marketCode} 등록 중단: {reason}", DateTimeOffset.UtcNow));
                    continue;
                }

                var marketPayload = payload with
                {
                    ResolvedOutboundPlaceCode = resolution.OutboundCode,
                    ResolvedReturnCenterCode = resolution.ReturnCode,
                };

                var result = await adapter.RegisterAsync(marketPayload, credential, ct);

                if (result.Success)
                {
                    listing.MarkRegistered(result.MarketItemId!, payload.SalePrice);
                    anySuccess = true;
                }
                else
                {
                    listing.MarkFailed($"{result.ErrorCode}: {result.ErrorMessage}");
                    failures.Add($"{marketCode}: {result.ErrorMessage}");
                }

                await listings.SaveAsync(ct);
                await bus.PublishAsync(new MarketplaceRegistered
                {
                    ProductId = product.Id,
                    ListingId = listing.Id,
                    MarketCode = marketCode,
                    Success = result.Success,
                    MarketItemId = result.MarketItemId,
                    Error = result.ErrorMessage,
                    TenantId = @event.TenantId,
                    CorrelationId = @event.JobId ?? @event.CorrelationId,
                    CausationId = @event.EventId,
                }, ct);

                notifier.Notify(new PipelineNotification(
                    @event.JobId ?? Guid.Empty, "Listing",
                    result.Success ? "Succeeded" : "Failed",
                    product.Id,
                    result.Success ? $"{marketCode} 등록 완료 (상품번호 {result.MarketItemId})" : $"{marketCode} 등록 실패: {result.ErrorMessage}",
                    DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Market} 등록 실패 (Product {ProductId})", marketCode, @event.ProductId);
                listing.MarkFailed(ex.Message);
                await listings.SaveAsync(ct);
                failures.Add($"{marketCode}: {ex.Message}");
                notifier.Notify(new PipelineNotification(
                    @event.JobId ?? Guid.Empty, "Listing", "Failed", product.Id,
                    $"{marketCode}: {ex.Message}", DateTimeOffset.UtcNow));
            }
        }

        if (anySuccess && product.Status == ProductStatus.Ready)
        {
            product.TransitionTo(ProductStatus.Listed);
            await products.SaveAsync(ct);
        }

        await FinishJobAsync(@event.JobId, anySuccess, string.Join(" / ", failures), ct);
    }

    /// <summary>
    /// 자동 등록(빠른 등록) Job의 마지막 상태를 확정한다.
    /// 이걸 하지 않으면 Job이 Listing/Running에 영원히 머물러
    /// 화면에서 "아직 진행 중"으로 보인다.
    /// </summary>
    private async Task FinishJobAsync(Guid? jobId, bool success, string error, CancellationToken ct)
    {
        if (jobId is not { } id) return;

        if (success) await progress.CompleteAsync(id, "마켓 등록 완료", ct);
        else await progress.ListingFailedAsync(id, error.Length > 0 ? error : "마켓 등록 실패", ct);
    }
}
