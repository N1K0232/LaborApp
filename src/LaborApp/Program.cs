using System.Diagnostics;
using System.Net.Mime;
using System.Text.Json.Serialization;
using FluentValidation.AspNetCore;
using LaborApp.BusinessLayer.HealthChecks;
using LaborApp.BusinessLayer.Settings;
using LaborApp.DataAccessLayer;
using LaborApp.Exceptions;
using LaborApp.Extensions;
using LaborApp.Swagger;
using MicroElements.Swashbuckle.FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using MinimalHelpers.OpenApi;
using MinimalHelpers.Routing;
using OperationResults.AspNetCore.Http;
using Serilog;
using TinyHelpers.AspNetCore.Extensions;
using TinyHelpers.AspNetCore.Swagger;
using TinyHelpers.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder.Services, builder.Configuration, builder.Environment, builder.Host);

var app = builder.Build();
Configure(app, app.Services, app.Environment);

await app.RunAsync();

void ConfigureServices(IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment, IHostBuilder host)
{
    var appSettings = services.ConfigureAndGet<AppSettings>(configuration, nameof(AppSettings));
    var swaggerSettings = services.ConfigureAndGet<SwaggerSettings>(configuration, nameof(SwaggerSettings));

    host.UseSerilog((hostingContext, loggerConfiguration) =>
    {
        loggerConfiguration.ReadFrom.Configuration(hostingContext.Configuration);
    });

    services.AddRequestLocalization(appSettings.SupportedCultures);
    services.AddWebOptimizer(minifyCss: true, minifyJavaScript: environment.IsProduction());

    services.AddHttpContextAccessor();
    services.AddMemoryCache();

    services.AddExceptionHandler<DefaultExceptionHandler>();

    services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
        options.SerializerOptions.Converters.Add(new UtcDateTimeConverter());
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = context =>
        {
            var statusCode = context.ProblemDetails.Status.GetValueOrDefault(StatusCodes.Status500InternalServerError);
            context.ProblemDetails.Type ??= $"https://httpstatuses.io/{statusCode}";
            context.ProblemDetails.Title ??= ReasonPhrases.GetReasonPhrase(statusCode);
            context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        };
    });

    services.AddOperationResult(options =>
    {
        options.ErrorResponseFormat = ErrorResponseFormat.List;
    });

    services.AddFluentValidationAutoValidation(options =>
    {
        options.DisableDataAnnotationsValidation = true;
    });

    if (swaggerSettings.Enabled)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Labor Api", Version = "v1" });
            options.AddDefaultResponse();
            options.AddAcceptLanguageHeader();
            options.AddFormFile();

            options.MapType<DateTime>(() => new OpenApiSchema
            {
                Type = "string",
                Format = "date-time",
                Example = new OpenApiString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
            });

            options.MapType<DateOnly>(() => new OpenApiSchema
            {
                Type = "string",
                Format = "date",
                Example = new OpenApiString(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"))
            });
        })
        .AddFluentValidationRulesToSwagger(options =>
        {
            options.SetNotNullableIfMinLengthGreaterThenZero = true;
        });
    }

    services.AddRazorPages();

    services.AddDbContext<IDataContext, DataContext>(options =>
    {
        var connectionString = configuration.GetConnectionString("SqlConnection");
        options.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(appSettings.MaxRetryCount, appSettings.MaxRetryDelay, null);
            sqlOptions.CommandTimeout(appSettings.CommandTimeout);
        });
    });

    services.AddHealthChecks().AddCheck<SqlConnectionHealthCheck>("sql", timeout: TimeSpan.FromMinutes(1));
}

void Configure(IApplicationBuilder app, IServiceProvider services, IWebHostEnvironment environment)
{
    var appSettings = services.GetRequiredService<IOptions<AppSettings>>().Value;
    var swaggerSettings = services.GetRequiredService<IOptions<SwaggerSettings>>().Value;

    environment.ApplicationName = appSettings.ApplicationName;

    app.UseHttpsRedirection();
    app.UseRequestLocalization();

    app.UseRouting();
    app.UseWebOptimizer();

    app.UseWhen(context => context.IsWebRequest(), builder =>
    {
        if (!environment.IsDevelopment())
        {
            builder.UseExceptionHandler("/Errors/500");
            builder.UseHsts();
        }

        builder.UseStatusCodePagesWithReExecute("/Errors/{0}");
    });

    app.UseWhen(context => context.IsApiRequest(), builder =>
    {
        builder.UseExceptionHandler();
        builder.UseStatusCodePages();
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();

    if (swaggerSettings.Enabled)
    {
        app.UseMiddleware<SwaggerBasicAuthenticationMiddleware>();

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Labor Api v1");
            options.InjectStylesheet("/css/swagger.css");
        });
    }

    app.UseSerilogRequestLogging();
    app.UseAuthorization();

    app.UseEndpoints(endpoints =>
    {
        endpoints.MapEndpoints();
        endpoints.MapRazorPages();
        endpoints.MapHealthChecks("/status",
                   new HealthCheckOptions
                   {
                       ResponseWriter = async (context, report) =>
                       {
                           var result = System.Text.Json.JsonSerializer.Serialize(
                               new
                               {
                                   status = report.Status.ToString(),
                                   details = report.Entries.Select(e => new
                                   {
                                       service = e.Key,
                                       status = Enum.GetName(typeof(HealthStatus), e.Value.Status),
                                       description = e.Value.Description
                                   })
                               });

                           context.Response.ContentType = MediaTypeNames.Application.Json;
                           await context.Response.WriteAsync(result);
                       }
                   });
    });
}