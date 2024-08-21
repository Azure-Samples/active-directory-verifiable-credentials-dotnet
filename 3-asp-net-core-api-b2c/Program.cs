using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Http;

using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace B2CVerifiedID {
    public class Program {
    public static void Main( string[] args ) {

        var builder = WebApplication.CreateBuilder( args );

        // This is required to be instantiated before the OpenIdConnectOptions starts getting configured.
        // By default, the claims mapping will map claim names in the old format to accommodate older SAML applications.
        // For instance, 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' instead of 'roles' claim.
        // This flag ensures that the ClaimsIdentity claims collection will be built from the claims in the token
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

        builder.Services.Configure<CookiePolicyOptions>( options =>
        {
            // This lambda determines whether user consent for non-essential cookies is needed for a given request.
            options.CheckConsentNeeded = context => true;
            options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
            // Handling SameSite cookie according to https://learn.microsoft.com/aspnet/core/security/samesite?view=aspnetcore-3.1
            options.HandleSameSiteCookieCompatibility();
        } );

        builder.Services.AddAuthentication( OpenIdConnectDefaults.AuthenticationScheme )
                .AddMicrosoftIdentityWebApp( builder.Configuration, "AzureAdB2C" );
        //builder.Services.AddMicrosoftIdentityWebAppAuthentication( builder.Configuration, "AzureAdB2C" );

        builder.Services.AddInMemoryTokenCaches();
        builder.Services.AddControllersWithViews()
                .AddMicrosoftIdentityUI();
        builder.Services.AddRazorPages();

        builder.Services.AddOptions();
        builder.Services.Configure<OpenIdConnectOptions>( builder.Configuration.GetSection( "AzureAdB2C" ) );

        builder.Services.AddMvc();

        builder.Services.AddCors( options => {
            options.AddPolicy( name: "B2CCorsCustomHtml", policy => {
                policy.WithOrigins( $"https://{builder.Configuration["AzureAdB2C:B2CName"]}.b2clogin.com" )
                        .WithMethods("GET", "OPTIONS");
            });
        });

        var app = builder.Build();

        //+ Added for Entra Verified ID so that callback uses proxy's hostname
        app.UseForwardedHeaders( new ForwardedHeadersOptions {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
        } );
        //- 

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment()) {
            app.UseExceptionHandler( "/Home/Error" );
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseStaticFiles();

        app.UseRouting();
        app.UseCors( "B2CCorsCustomHtml" );

        app.UseAuthentication();
        app.UseAuthorization();
        
        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}" );
        app.MapRazorPages();

        System.Environment.SetEnvironmentVariable( "API-KEY", Guid.NewGuid().ToString() );
            
        app.Run();
    }
}
}
