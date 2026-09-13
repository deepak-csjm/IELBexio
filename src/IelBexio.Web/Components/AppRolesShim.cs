namespace IelBexio.Web.Components;

/// <summary>
/// Re-exports the application role names for Razor components. Razor cannot resolve
/// <c>IelBexio.Application.Abstractions.AppRoles</c> without an ambiguous using, and a shim is clearer
/// than importing the whole namespace into every component.
/// </summary>
public static class AppRolesShim
{
    public const string Admin = IelBexio.Application.Abstractions.AppRoles.Admin;
    public const string Reviewer = IelBexio.Application.Abstractions.AppRoles.Reviewer;
    public const string Approver = IelBexio.Application.Abstractions.AppRoles.Approver;
    public const string IntegrationManager = IelBexio.Application.Abstractions.AppRoles.IntegrationManager;
    public const string ReadOnly = IelBexio.Application.Abstractions.AppRoles.ReadOnly;
}
