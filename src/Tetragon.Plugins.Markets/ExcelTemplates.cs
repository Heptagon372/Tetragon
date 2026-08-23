using System.Text;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Markets;

/// <summary>양식 구현이 공통으로 쓰는 헬퍼.</summary>
internal static class TemplateHelpers
{
    /// <summary>
    /// 위탁판매 출고지 — 공급처 주소가 있으면 그것을, 없으면 판매자 기본값을 쓴다.
    /// 엑셀은 코드가 아닌 주소 문자열을 그대로 받으므로 쿠팡 API보다 단순하다.
    /// </summary>
    // 마켓에 실제로 등록되는 출고지와 같은 값을 쓴다 (우편번호와 짝이 맞는 주소)
    public static string OutboundAddress(ExcelRowContext ctx, string settingKey = "outbound_address") =>
        ctx.Payload.Logistics.OutboundPlace.Address ?? ctx.Setting(settingKey);

    public static string ReturnAddress(ExcelRowContext ctx, string settingKey = "return_address")
    {
        var logistics = ctx.Payload.Logistics;
        if (string.IsNullOrWhiteSpace(logistics.ReturnAddress)) return ctx.Setting(settingKey);
        // 우편번호가 있으면 함께 표기해 업로드 시 주소 검색이 쉽도록 한다
        return string.IsNullOrWhiteSpace(logistics.ReturnZipcode)
            ? logistics.ReturnAddress
            : $"({logistics.ReturnZipcode}) {logistics.ReturnAddress}";
    }

    /// <summary>공급처가 청구하는 반품비 우선.</summary>
    public static string ReturnFee(ExcelRowContext ctx, string fallback = "5000") =>
        ctx.Payload.Logistics.ReturnFee is { } fee
            ? ((int)fee).ToString()
            : ctx.Setting("return_fee", fallback);

    /// <summary>공급처 연락처 우선 (반품 문의가 공급처로 가야 한다).</summary>
    public static string ContactNumber(ExcelRowContext ctx, string settingKey = "as_telephone") =>
        ctx.Payload.Logistics.ReturnPhone
        ?? ctx.Payload.Logistics.SupplierPhone
        ?? ctx.Setting(settingKey, "010-0000-0000");

    /// <summary>
    /// 상세설명 HTML.
    /// 공급처가 이미지 재사용을 허용한 경우에만 상세 HTML을 그대로 쓰고,
    /// 아니면 대표 이미지만으로 구성한다 (저작권).
    /// </summary>
    public static string DetailHtml(ListingPayload payload)
    {
        if (payload.Logistics is { DetailImagesAllowed: true, DetailHtml: { Length: > 0 } supplierHtml })
            return supplierHtml;

        var sb = new StringBuilder();
        sb.Append("<div style=\"text-align:center\">");
        sb.Append($"<p>{System.Net.WebUtility.HtmlEncode(payload.Name)}</p>");
        foreach (var url in payload.ImageUrls.Take(10))
            sb.Append($"<img src=\"{url}\" style=\"max-width:100%\" alt=\"\">");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>마켓에 표기할 출고 소요일 (공급처 평균 + 여유).</summary>
    public static int ShippingDays(ExcelRowContext ctx) => ctx.Payload.Logistics.OutboundShippingDays;

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>"색상:블랙,사이즈:L" 형태의 옵션 표기.</summary>
    public static string OptionText(PayloadVariant variant) =>
        string.Join(",", variant.Options.Select(kv => $"{kv.Key}:{kv.Value}"));

    public static string ExtraImages(ListingPayload payload, int take) =>
        string.Join(",", payload.ImageUrls.Skip(1).Take(take));
}

// ─────────────────────────────────────────────────────────────────────────
// ESM Plus (옥션 + G마켓 통합 대량등록)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// ESM Plus 대량등록 엑셀 양식.
/// 옥션과 G마켓을 한 파일로 동시 등록할 수 있는 것이 ESM Plus의 핵심 이점이다.
/// </summary>
public sealed class EsmPlusExcelTemplate : IMarketExcelTemplate
{
    public string MarketCode => "esmplus";
    public string DisplayName => "ESM Plus (옥션+G마켓)";
    public string FileNameHint => "ESM_대량등록";

    public string UploadGuide =>
        "ESM Plus(esmplus.com) 로그인 → 상품등록/변경 → 대량 상품 등록 → 엑셀 업로드. " +
        "업로드 전 '카테고리코드' 열을 ESM Plus의 실제 전시카테고리 코드로 채워야 합니다. " +
        "옥션과 G마켓에 동시 등록됩니다.";

    public IReadOnlyList<ExcelColumn> Columns =>
    [
        new("사이트구분", 12, true, "A=옥션, G=G마켓, AG=동시"),
        new("카테고리코드", 16, true, "ESM Plus 전시카테고리 코드"),
        new("상품명", 45, true),
        new("판매가", 12, true),
        new("재고수량", 10, true),
        new("판매기간", 12),
        new("대표이미지", 40, true, "http로 시작하는 이미지 URL"),
        new("추가이미지", 40),
        new("상세설명", 60, true, "HTML"),
        new("옵션명", 24, false, "예: 색상,사이즈"),
        new("옵션값", 30, false, "예: 블랙,L"),
        new("옵션가", 12),
        new("옵션재고", 10),
        new("배송비종류", 12, true),
        new("배송비", 10),
        new("반품배송비", 12),
        new("교환배송비", 12),
        new("발송지주소", 30),
        new("반품지주소", 30),
        new("원산지", 14, true),
        new("제조사", 18),
        new("브랜드", 18),
        new("미성년자구매", 12),
        new("과세여부", 10),
    ];

    public IEnumerable<IReadOnlyList<object?>> BuildRows(ExcelRowContext context)
    {
        var p = context.Payload;
        var site = context.Setting("site_type", "AG");
        var category = p.MarketCategoryCodes.GetValueOrDefault("esmplus")
                       ?? context.Setting("default_category_code");
        var origin = context.Setting("origin", "중국");
        var basePrice = (int)p.SalePrice.Amount;

        // 옵션이 없으면 1행, 있으면 옵션마다 1행
        if (p.Variants.Count <= 1)
        {
            yield return
            [
                site, category, TemplateHelpers.Truncate(p.Name, 100), basePrice, p.Stock, "365",
                p.ImageUrls.FirstOrDefault() ?? "", TemplateHelpers.ExtraImages(p, 9),
                TemplateHelpers.DetailHtml(p),
                "", "", "", "",
                context.Setting("delivery_fee_type", "선불"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                TemplateHelpers.OutboundAddress(context), TemplateHelpers.ReturnAddress(context),
                origin, context.Setting("manufacturer", "제조사 참조"),
                p.Attributes.GetValueOrDefault("브랜드", "브랜드 없음"),
                "가능", "과세",
            ];
            yield break;
        }

        var optionNames = string.Join(",", p.OptionGroups.Select(g => g.Name));
        var first = true;
        foreach (var variant in p.Variants)
        {
            yield return
            [
                site, category, TemplateHelpers.Truncate(p.Name, 100), basePrice, p.Stock, "365",
                // 첫 행에만 상품 공통 정보를 넣는 것이 ESM 양식 관례
                first ? p.ImageUrls.FirstOrDefault() ?? "" : "",
                first ? TemplateHelpers.ExtraImages(p, 9) : "",
                first ? TemplateHelpers.DetailHtml(p) : "",
                optionNames,
                string.Join(",", variant.Options.Values),
                (int)(variant.Price.Amount - p.SalePrice.Amount),
                variant.Stock,
                context.Setting("delivery_fee_type", "선불"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                TemplateHelpers.OutboundAddress(context), TemplateHelpers.ReturnAddress(context),
                origin, context.Setting("manufacturer", "제조사 참조"),
                p.Attributes.GetValueOrDefault("브랜드", "브랜드 없음"),
                "가능", "과세",
            ];
            first = false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
// 11번가
// ─────────────────────────────────────────────────────────────────────────

public sealed class ElevenStExcelTemplate : IMarketExcelTemplate
{
    public string MarketCode => "11st";
    public string DisplayName => "11번가";
    public string FileNameHint => "11번가_대량등록";

    public string UploadGuide =>
        "11번가 셀러오피스 → 상품관리 → 대량 상품등록 → 엑셀 업로드. " +
        "'전시카테고리번호'는 셀러오피스의 카테고리 조회에서 확인한 dispCtgrNo를 넣으세요.";

    public IReadOnlyList<ExcelColumn> Columns =>
    [
        new("전시카테고리번호", 18, true),
        new("상품명", 45, true),
        new("판매가", 12, true),
        new("재고수량", 10, true),
        new("상품상태", 12, true, "새상품/중고"),
        new("대표이미지URL", 40, true),
        new("추가이미지URL", 40),
        new("상세설명", 60, true),
        new("옵션명칭", 24),
        new("옵션값", 30),
        new("옵션추가금액", 14),
        new("옵션재고", 10),
        new("배송비유형", 12, true),
        new("배송비", 10),
        new("반품배송비", 12),
        new("교환배송비", 12),
        new("발송지코드", 14),
        new("반품지코드", 14),
        new("원산지", 14, true),
        new("브랜드", 18),
        new("A/S안내", 30),
        new("해외구매대행여부", 16, true),
    ];

    public IEnumerable<IReadOnlyList<object?>> BuildRows(ExcelRowContext context)
    {
        var p = context.Payload;
        var category = p.MarketCategoryCodes.GetValueOrDefault("11st")
                       ?? context.Setting("default_category_code");

        if (p.Variants.Count <= 1)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)p.SalePrice.Amount, p.Stock, "새상품",
                p.ImageUrls.FirstOrDefault() ?? "", TemplateHelpers.ExtraImages(p, 9),
                TemplateHelpers.DetailHtml(p),
                "", "", "", "",
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                TemplateHelpers.OutboundAddress(context, "outbound_address_seq"), TemplateHelpers.ReturnAddress(context, "return_address_seq"),
                context.Setting("origin", "중국"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                context.Setting("as_guide", "판매자 문의"),
                context.Setting("is_abroad", "Y"),
            ];
            yield break;
        }

        var optionNames = string.Join(",", p.OptionGroups.Select(g => g.Name));
        var first = true;
        foreach (var variant in p.Variants)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)p.SalePrice.Amount, p.Stock, "새상품",
                first ? p.ImageUrls.FirstOrDefault() ?? "" : "",
                first ? TemplateHelpers.ExtraImages(p, 9) : "",
                first ? TemplateHelpers.DetailHtml(p) : "",
                optionNames, string.Join(",", variant.Options.Values),
                (int)(variant.Price.Amount - p.SalePrice.Amount), variant.Stock,
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                TemplateHelpers.OutboundAddress(context, "outbound_address_seq"), TemplateHelpers.ReturnAddress(context, "return_address_seq"),
                context.Setting("origin", "중국"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                context.Setting("as_guide", "판매자 문의"),
                context.Setting("is_abroad", "Y"),
            ];
            first = false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
// 쿠팡
// ─────────────────────────────────────────────────────────────────────────

public sealed class CoupangExcelTemplate : IMarketExcelTemplate
{
    public string MarketCode => "coupang";
    public string DisplayName => "쿠팡";
    public string FileNameHint => "쿠팡_대량등록";

    public string UploadGuide =>
        "쿠팡 WING → 상품관리 → 상품 일괄등록 → 엑셀 업로드. " +
        "'노출카테고리코드'는 WING의 카테고리 추천/조회에서 확인하세요. " +
        "구매대행 상품은 '해외구매대행'을 Y로 두고 개인통관고유부호 수집이 필요합니다.";

    public IReadOnlyList<ExcelColumn> Columns =>
    [
        new("노출카테고리코드", 18, true),
        new("등록상품명", 45, true),
        new("판매가격", 12, true),
        new("정가", 12),
        new("재고수량", 10, true),
        new("대표이미지", 40, true),
        new("추가이미지", 40),
        new("상세설명", 60, true),
        new("옵션명", 24),
        new("옵션값", 30),
        new("옵션가격", 12),
        new("옵션재고", 10),
        new("배송방법", 14, true, "구매대행=AGENT_BUY"),
        new("택배사코드", 14),
        new("배송비종류", 14, true),
        new("기본배송비", 12),
        new("반품배송비", 12),
        new("출고지코드", 14, true),
        new("반품지코드", 14, true),
        new("원산지", 14, true),
        new("브랜드", 18),
        new("해외구매대행", 14, true),
        new("병행수입", 12),
        new("미성년자구매", 12),
    ];

    public IEnumerable<IReadOnlyList<object?>> BuildRows(ExcelRowContext context)
    {
        var p = context.Payload;
        var category = p.MarketCategoryCodes.GetValueOrDefault("coupang")
                       ?? context.Setting("default_category_code");
        var listPrice = (int)(p.SalePrice.Amount * 1.2m / 10) * 10;   // 정가는 판매가의 120% 근사

        var variants = p.Variants.Count > 0 ? p.Variants : [];
        if (variants.Count <= 1)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)p.SalePrice.Amount, listPrice, p.Stock,
                p.ImageUrls.FirstOrDefault() ?? "", TemplateHelpers.ExtraImages(p, 9),
                TemplateHelpers.DetailHtml(p),
                "", "", "", "",
                "AGENT_BUY", context.Setting("delivery_company_code", "CJGLS"),
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                context.Setting("return_fee", "5000"),
                TemplateHelpers.OutboundAddress(context, "outbound_shipping_place_code"), TemplateHelpers.ReturnAddress(context, "return_center_code"),
                context.Setting("origin", "중국"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                "Y", "N", "Y",
            ];
            yield break;
        }

        var optionNames = string.Join(",", p.OptionGroups.Select(g => g.Name));
        var first = true;
        foreach (var variant in variants)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)variant.Price.Amount, listPrice, variant.Stock,
                first ? p.ImageUrls.FirstOrDefault() ?? "" : "",
                first ? TemplateHelpers.ExtraImages(p, 9) : "",
                first ? TemplateHelpers.DetailHtml(p) : "",
                optionNames, string.Join(",", variant.Options.Values),
                (int)variant.Price.Amount, variant.Stock,
                "AGENT_BUY", context.Setting("delivery_company_code", "CJGLS"),
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                context.Setting("return_fee", "5000"),
                TemplateHelpers.OutboundAddress(context, "outbound_shipping_place_code"), TemplateHelpers.ReturnAddress(context, "return_center_code"),
                context.Setting("origin", "중국"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                "Y", "N", "Y",
            ];
            first = false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
// 스마트스토어
// ─────────────────────────────────────────────────────────────────────────

public sealed class SmartStoreExcelTemplate : IMarketExcelTemplate
{
    public string MarketCode => "smartstore";
    public string DisplayName => "네이버 스마트스토어";
    public string FileNameHint => "스마트스토어_대량등록";

    public string UploadGuide =>
        "스마트스토어센터 → 상품관리 → 상품 일괄등록 → 엑셀 업로드. " +
        "'카테고리ID'는 스마트스토어의 리프(최하위) 카테고리 ID여야 합니다.";

    public IReadOnlyList<ExcelColumn> Columns =>
    [
        new("카테고리ID", 16, true, "리프 카테고리만 가능"),
        new("상품명", 45, true),
        new("판매가", 12, true),
        new("재고수량", 10, true),
        new("대표이미지", 40, true),
        new("추가이미지", 40),
        new("상세설명", 60, true),
        new("옵션명", 24),
        new("옵션값", 30),
        new("옵션가", 12),
        new("옵션재고", 10),
        new("배송비유형", 14, true),
        new("기본배송비", 12),
        new("반품배송비", 12),
        new("교환배송비", 12),
        new("원산지코드", 14, true),
        new("수입사", 18),
        new("브랜드", 18),
        new("A/S전화번호", 16, true),
        new("A/S안내", 30, true),
        new("미성년자구매", 12),
    ];

    public IEnumerable<IReadOnlyList<object?>> BuildRows(ExcelRowContext context)
    {
        var p = context.Payload;
        var category = p.MarketCategoryCodes.GetValueOrDefault("smartstore")
                       ?? context.Setting("default_category_id");

        if (p.Variants.Count <= 1)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)p.SalePrice.Amount, p.Stock,
                p.ImageUrls.FirstOrDefault() ?? "", TemplateHelpers.ExtraImages(p, 9),
                TemplateHelpers.DetailHtml(p),
                "", "", "", "",
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                context.Setting("origin_code", "0200037"),
                context.Setting("importer", "직수입"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                TemplateHelpers.ContactNumber(context),
                context.Setting("as_guide", "구매자 문의는 스토어 문의하기를 이용해주세요."),
                "가능",
            ];
            yield break;
        }

        var optionNames = string.Join(",", p.OptionGroups.Select(g => g.Name));
        var first = true;
        foreach (var variant in p.Variants)
        {
            yield return
            [
                category, TemplateHelpers.Truncate(p.Name, 100), (int)p.SalePrice.Amount, p.Stock,
                first ? p.ImageUrls.FirstOrDefault() ?? "" : "",
                first ? TemplateHelpers.ExtraImages(p, 9) : "",
                first ? TemplateHelpers.DetailHtml(p) : "",
                optionNames, string.Join(",", variant.Options.Values),
                (int)(variant.Price.Amount - p.SalePrice.Amount), variant.Stock,
                context.Setting("delivery_fee_type", "무료"), context.Setting("delivery_fee", "0"),
                TemplateHelpers.ReturnFee(context), context.Setting("exchange_fee", "10000"),
                context.Setting("origin_code", "0200037"),
                context.Setting("importer", "직수입"),
                p.Attributes.GetValueOrDefault("브랜드", ""),
                TemplateHelpers.ContactNumber(context),
                context.Setting("as_guide", "구매자 문의는 스토어 문의하기를 이용해주세요."),
                "가능",
            ];
            first = false;
        }
    }
}
