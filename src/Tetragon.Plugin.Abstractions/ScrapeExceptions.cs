namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 재시도해도 결과가 달라지지 않는 수집 실패 (삭제·품절된 상품, 잘못된 URL 등).
/// 파이프라인은 이 예외에 대해 재시도하지 않고 즉시 실패 처리한다.
/// </summary>
public sealed class PermanentScrapeException(string message) : Exception(message);

/// <summary>
/// 일시적 수집 실패 (네트워크 오류, 레이트리밋, 일시 차단).
/// 지수 백오프 재시도 대상.
/// </summary>
public sealed class TransientScrapeException(string message, Exception? inner = null)
    : Exception(message, inner);
