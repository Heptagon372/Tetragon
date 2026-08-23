namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 모든 플러그인의 베이스 계약 (설계서 ADR-005).
/// 새 공급처/마켓/AI Provider는 이 계약 구현 + DI 등록만으로 추가된다. 기존 코드 무수정(OCP).
/// </summary>
public interface IPlugin
{
    /// <summary>플러그인 고유 코드 (예: "taobao", "smartstore", "claude").</summary>
    string Code { get; }

    /// <summary>표시 이름.</summary>
    string DisplayName { get; }

    /// <summary>시맨틱 버전. 파서 버전 관리(설계서 5.1)에 사용.</summary>
    string Version { get; }

    /// <summary>실제 외부 연동 여부 (false = 시뮬레이션).</summary>
    bool IsLive { get; }

    /// <summary>
    /// 지금 요청을 처리할 수 있는가 (자격증명 구비 등).
    /// IsLive와 분리된 개념 — 실연동 플러그인이라도 키가 없으면 false.
    /// 라우터는 IsAvailable == false인 플러그인을 후보에서 제외한다.
    /// </summary>
    bool IsAvailable => true;
}
