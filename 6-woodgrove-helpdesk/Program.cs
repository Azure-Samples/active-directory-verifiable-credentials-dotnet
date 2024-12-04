using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

namespace WoodgroveHelpdesk {
    public class Program {
        public static void Main( string[] args ) {
            var builder = WebApplication.CreateBuilder( args );

            // Add services to the container.
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorPages();
            builder.Services.AddHttpClient();
            builder.Services.AddSession( options => {
                options.IdleTimeout = TimeSpan.FromMinutes( 1 );//You can set Time   
                options.Cookie.IsEssential = true;
                options.Cookie.HttpOnly = true;
            } );

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
            app.UseSession();

            app.MapControllerRoute( name: "default", pattern: "{controller=Home}/{action=Index}/{id?}" );
            app.MapRazorPages();

            // generate an api-key on startup that we can use to validate callbacks
            System.Environment.SetEnvironmentVariable( "API-KEY", Guid.NewGuid().ToString() );

            app.Run();
        }
    }
}