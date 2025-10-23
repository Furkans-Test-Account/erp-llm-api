using Api.DTOs;

namespace Api.Services.Abstractions;

public interface IQuestionRouter
{
    /// Soruyu pack’lere yönlendirir (şimdilik anahtar kelime + basit sezgisel)
    RouteResponseDto Route(RouteRequestDto req, SliceResultDto slice);
}
