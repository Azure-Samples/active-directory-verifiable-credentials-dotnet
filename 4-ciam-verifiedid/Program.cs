using System;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace CiamVerifiedID {
    public class Program {
        public static void Main( string[] args ) {

            var builder = WebApplication.CreateBuilder( args );

            // This is required to be instantiated before the OpenIdConnectOptions starts getting configured.
            // By default, the claims mapping will map claim names in the old format to accommodate older SAML applications.
            // For instance, 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' instead of 'roles' claim.
            // This flag ensures that the ClaimsIdentity claims collection will be built from the claims in the token
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

            // Add services to the container.
            builder.Services.AddAuthentication( OpenIdConnectDefaults.AuthenticationScheme )
                .AddMicrosoftIdentityWebApp( builder.Configuration.GetSection( "AzureAd" ) );
            builder.Services.AddInMemoryTokenCaches();
            builder.Services.AddControllersWithViews( options => {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.Filters.Add( new AuthorizeFilter( policy ) );
            } );
            builder.Services.AddRazorPages()
                .AddMicrosoftIdentityUI();

            builder.Services.AddMvc();

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