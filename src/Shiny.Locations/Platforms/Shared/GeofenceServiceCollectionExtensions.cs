﻿#if PLATFORM
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Locations;
using Shiny.Locations.Infrastructure;
using Shiny.Stores;
#if ANDROID
using Android.App;
using Android.Gms.Common;
#endif

namespace Shiny;


public static class GeofenceServiceCollectionExtensions
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="services"></param>
    /// <param name="delegateType"></param>
    /// <returns></returns>
    public static IServiceCollection AddGeofencing(this IServiceCollection services, Type delegateType)
    {
        services.AddRepository<GeofenceRegionStoreConverter, GeofenceRegion>();
#if ANDROID
        var resultCode = GoogleApiAvailability
            .Instance
            .IsGooglePlayServicesAvailable(Application.Context);

        if (resultCode == ConnectionResult.ServiceMissing)
            return services.AddGpsDirectGeofencing(delegateType);

        services.AddShinyService(delegateType);
        services.AddShinyService<GeofenceManager>();
#elif APPLE
        services.AddShinyService(delegateType);
        services.AddShinyService<GeofenceManager>();
#endif
        return services;
    }


    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddGeofencing<T>(this IServiceCollection services) where T : class, IGeofenceDelegate
        => services.AddGeofencing(typeof(T));


    /// <summary>
    /// This uses background GPS in realtime broadcasts to monitor geofences - DO NOT USE THIS IF YOU DON"T KNOW WHAT YOU ARE DOING
    /// It is potentially hostile to battery life
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddGpsDirectGeofencing<T>(this IServiceCollection services) where T : class, IGeofenceDelegate
        => services.AddGpsDirectGeofencing(typeof(T));


    /// <summary>
    /// This uses background GPS in realtime broadcasts to monitor geofences - DO NOT USE THIS IF YOU DON"T KNOW WHAT YOU ARE DOING
    /// It is potentially hostile to battery life
    /// </summary>
    /// <param name="services"></param>
    /// <param name="delegateType"></param>
    /// <returns></returns>
    public static IServiceCollection AddGpsDirectGeofencing(this IServiceCollection services, Type delegateType)
    {
        services.AddShinyService(delegateType);
        services.AddShinyService<GpsGeofenceManagerImpl>();
        return services;
    }
}
#endif