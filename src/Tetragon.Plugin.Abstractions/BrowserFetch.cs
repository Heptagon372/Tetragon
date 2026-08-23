namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// TLS 지문·JS 챌린지로 HttpClient가 차단되는 사이트를 실제 브라우저 엔진으로 가져온다.
///
/// 배경: 공급처 수집의 기본 경로는 <c>HttpClient("scraper")</c>지만, 아마존/Akamai는
/// 헤더가 아니라 그 아래 계층(TLS(JA3/JA4) 지문, JS 센서 실행)으로 봇을 걸러낸다.
/// 같은 헤더를 보내도 브라우저는 통과하고 .NET HttpClient는 캡차를 받는다
/// (AmazonSupplierPlugin 주석의 실측 참조). 이 포트는 그 계층을 넘기 위해
/// 외부 fetch 사이드카(Playwright)를 호출한다.
///
/// 구현은 Infrastructure에 있고(사이드카 REST 호출), 플러그인은 이 계약만 안다(OCP).
/// 사이드카가 미구성이면 <see cref="IsAvailable"/> == false → 플러그인은 직접 경로만 쓴다
/// (안전한 기본값: 미설정 시 시스템 동작이 도입 전과 100% 동일하다).
///
/// 성능/비용 주의: 브라우저 fetch는 직접 HTTP보다 10~50배 느리고 비싸다
/// (프록시 트래픽 + 캡차 비용). 그래서 플러그인은 "직접 시도 → 차단 감지 → 브라우저 폴백"
/// 순서로 쓴다. 안 걸리는 요청까지 브라우저로 돌리지 않는다.
/// </summary>
public interface IBrowserFetcher
{
    /// <summary>사이드카가 구성되어 지금 호출 가능한가. false면 플러그인은 폴백하지 않는다.</summary>
    bool IsAvailable { get; }

    /// <summary>URL 하나를 브라우저 엔진으로 가져온다. 모든 재시도 소진 후에도 챌린지면 <see cref="BrowserFetchResult.Blocked"/>가 true.</summary>
    Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest request, CancellationToken ct);
}

/// <summary>브라우저 fetch 요청.</summary>
public sealed record BrowserFetchRequest
{
    public required Uri Url { get; init; }

    /// <summary>DOM 준비 판정용 CSS 셀렉터 (예: 상품명 요소). null이면 networkidle까지 대기.</summary>
    public string? WaitForSelector { get; init; }

    /// <summary>행동 시뮬레이션 강도. Akamai 행동분석(4단계) 대응.</summary>
    public BehaviorProfile Behavior { get; init; } = BehaviorProfile.Human;

    /// <summary>프록시 지역/세션 정책. 공급처별로 다름(예: amazon.com→US, coupang→KR).</summary>
    public string ProxyPolicy { get; init; } = "residential-kr";

    /// <summary>캡차 자동 풀이 허용 여부(비용 발생). 기본 끔.</summary>
    public bool AllowCaptchaSolve { get; init; }

    /// <summary>같은 상품 여러 요청을 한 세션으로 묶기 위한 키(쿠키/IP 스티키). _abck 재획득 비용 절감.</summary>
    public string? SessionKey { get; init; }

    /// <summary>주입할 쿠키(직접 경로에서 이미 확보한 세션 등).</summary>
    public string? CookieHeader { get; init; }
}

/// <summary>행동 시뮬레이션 강도.</summary>
public enum BehaviorProfile
{
    /// <summary>즉시 HTML만. 챌린지 없는 사이트용.</summary>
    None,
    /// <summary>가벼운 대기·스크롤. 저비용.</summary>
    Light,
    /// <summary>사람 유사 — 무작위 대기, 마우스 이동, 불규칙 스크롤, 타이핑 지연.</summary>
    Human,
}

/// <summary>브라우저 fetch 결과.</summary>
public sealed record BrowserFetchResult
{
    public required int StatusCode { get; init; }
    public required string Html { get; init; }
    public string FinalUrl { get; init; } = "";

    /// <summary>세션 재사용용 쿠키(_abck 등). 다음 요청의 <see cref="BrowserFetchRequest.CookieHeader"/>로 되돌려줄 수 있다.</summary>
    public string? SetCookie { get; init; }

    /// <summary>캡차/챌린지를 실제로 풀었는지 (관측·비용 추적용).</summary>
    public bool ChallengeSolved { get; init; }

    /// <summary>사이드카가 판단한 차단 여부(모든 재시도·프록시 로테이트 소진 후에도 챌린지면 true).</summary>
    public bool Blocked { get; init; }
}
