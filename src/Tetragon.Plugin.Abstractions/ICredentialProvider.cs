namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 플러그인이 자격증명을 동기적으로 조회하기 위한 포트 (Infrastructure에서 캐시 기반 구현).
/// 마켓 어댑터는 호출 시점에 MarketCredential을 주입받지만, 공급처/AI 플러그인은
/// IsLive 판정 등 동기 컨텍스트가 필요하다.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>scope 예: "ai:claude", "supplier:taobao", "market:coupang".</summary>
    string? Get(string scope, string key);
    bool HasKey(string scope, string key);
}
