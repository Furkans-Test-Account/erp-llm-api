using Api.DTOs;

namespace Api.Services.Abstractions;

public interface ISchemaSlicer
{
    /// Şemayı pack’lere böler (ilişkileri korur, köprüleri ekler)
    SliceResultDto Slice(SchemaDto schema);
}
