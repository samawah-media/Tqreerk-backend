using Taqreerk.Application.DTOs.Admin;
using Microsoft.AspNetCore.Http;

namespace Taqreerk.Application.Interfaces;

public interface IAdminPartnersService
{
    Task<IReadOnlyList<AdminPartnerCategoryDto>> ListCategoriesAsync(CancellationToken ct = default);

    Task<AdminPartnerCategoryDto> CreateCategoryAsync(
        Guid actingUserId, CreatePartnerCategoryRequest req, CancellationToken ct = default);

    Task<AdminPartnerCategoryDto> UpdateCategoryAsync(
        Guid actingUserId, Guid id, UpdatePartnerCategoryRequest req, CancellationToken ct = default);

    Task DeleteCategoryAsync(Guid actingUserId, Guid id, CancellationToken ct = default);

    Task ReorderCategoriesAsync(Guid actingUserId, ReorderRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<AdminPartnerDto>> ListAsync(CancellationToken ct = default);

    Task<AdminPartnerDto> CreateAsync(
        Guid actingUserId, CreatePartnerRequest req, IFormFile? logo, CancellationToken ct = default);

    Task<AdminPartnerDto> UpdateAsync(
        Guid actingUserId, Guid id, UpdatePartnerRequest req, IFormFile? logo, CancellationToken ct = default);

    Task DeleteAsync(Guid actingUserId, Guid id, CancellationToken ct = default);

    Task ReorderAsync(Guid actingUserId, PartnerReorderRequest req, CancellationToken ct = default);
}
