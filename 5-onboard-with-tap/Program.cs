using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

namespace OnboardWithTAP {
    public class Program {
        public static void Main( string[] args ) {
            var builder = WebApplication.CreateBuilder( args );

            //+
            var allowedUserAdminRole = builder.Configuration["AzureAd:AllowedUserAdminRole"];
            if (!string.IsNullOrEmpty( allowedUserAdminRole )) {
                builder.Services.AddAuthorization( options => {
                    options.AddPolicy( "alloweduseradmins", policy => {
                        //policy.RequireAuthenticatedUser();
                        policy.RequireRole( allowedUserAdminRole.Split( ";" ) );
                    } );
                } );
            }
            //-

            // Add services to the container.
            builder.Services.AddAuthentication( OpenIdConnectDefaults.AuthenticationScheme )
                .AddMicrosoftIdentityWebApp( builder.Configuration.GetSection( "AzureAd" ) );

            builder.Services.AddControllersWithViews( options => {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.Filters.Add( new AuthorizeFilter( policy ) );
            } );

            builder.Services.AddRazorPages()
                .AddMicrosoftIdentityUI();

            var app = builder.Build();

            app.UseForwardedHeaders( new ForwardedHeadersOptions {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            } );

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment()) {
                app.UseExceptionHandler( "/Home/Error" );
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllerRoute( name: "default", pattern: "{controller=Home}/{action=Index}/{id?}" );
            app.MapRazorPages();

            // generate an api-key on startup that we can use to validate callbacks
            System.Environment.SetEnvironmentVariable( "API-KEY", Guid.NewGuid().ToString() );

            app.Run();
        }
    }
}