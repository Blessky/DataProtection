﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNet.Cryptography.Cng;
using Microsoft.AspNet.DataProtection;
using Microsoft.AspNet.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.Framework.Internal;
using Microsoft.AspNet.DataProtection.Cng;
using Microsoft.AspNet.DataProtection.KeyManagement;
using Microsoft.AspNet.DataProtection.Repositories;
using Microsoft.Framework.Logging;
using Microsoft.Win32;

namespace Microsoft.Framework.DependencyInjection
{
    /// <summary>
    /// Allows registering and configuring Data Protection in the application.
    /// </summary>
    public static class DataProtectionServiceCollectionExtensions
    {
        /// <summary>
        /// Adds default Data Protection services to an <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="services">The service collection to which to add DataProtection services.</param>
        /// <returns>The <paramref name="services"/> instance.</returns>
        public static IServiceCollection AddDataProtection([NotNull] this IServiceCollection services)
        {
            services.AddOptions();

            // The default key services are a strange beast. We don't want to return
            // IXmlEncryptor and IXmlRepository as-is because they almost always have to be
            // set as a matched pair. Instead, our built-in key manager will use a meta-service
            // which represents the default pairing (logic based on hosting environment as
            // demonstrated below), and if the developer explicitly specifies one or the other
            // we'll not use the fallback at all.
            services.TryAdd(ServiceDescriptor.Singleton<IDefaultKeyServices>(serviceProvider =>
            {
                ILogger log = serviceProvider.GetLogger(typeof(DataProtectionServices));

                ServiceDescriptor keyEncryptorDescriptor = null;
                ServiceDescriptor keyRepositoryDescriptor = null;

                // If we're running in Azure Web Sites, the key repository goes in the %HOME% directory.
                var azureWebSitesKeysFolder = FileSystemXmlRepository.GetKeyStorageDirectoryForAzureWebSites();
                if (azureWebSitesKeysFolder != null)
                {
                    if (log.IsInformationLevelEnabled())
                    {
                        log.LogInformationF($"Azure Web Sites environment detected. Using '{azureWebSitesKeysFolder.FullName}' as key repository; keys will not be encrypted at rest.");
                    }

                    // Cloud DPAPI isn't yet available, so we don't encrypt keys at rest.
                    // This isn't all that different than what Azure Web Sites does today, and we can always add this later.
                    keyRepositoryDescriptor = DataProtectionServiceDescriptors.IXmlRepository_FileSystem(azureWebSitesKeysFolder);
                }
                else
                {
                    // If the user profile is available, store keys in the user profile directory.
                    var localAppDataKeysFolder = FileSystemXmlRepository.DefaultKeyStorageDirectory;
                    if (localAppDataKeysFolder != null)
                    {
                        if (OSVersionUtil.IsWindows())
                        {
                            // If the user profile is available, we can protect using DPAPI.
                            // Probe to see if protecting to local user is available, and use it as the default if so.
                            keyEncryptorDescriptor = DataProtectionServiceDescriptors.IXmlEncryptor_Dpapi(protectToMachine: !DpapiSecretSerializerHelper.CanProtectToCurrentUserAccount());
                        }
                        keyRepositoryDescriptor = DataProtectionServiceDescriptors.IXmlRepository_FileSystem(localAppDataKeysFolder);

                        if (log.IsInformationLevelEnabled())
                        {
                            if (keyEncryptorDescriptor != null)
                            {
                                log.LogInformationF($"User profile is available. Using '{localAppDataKeysFolder.FullName}' as key repository and Windows DPAPI to encrypt keys at rest.");
                            }
                            else
                            {
                                log.LogInformationF($"User profile is available. Using '{localAppDataKeysFolder.FullName}' as key repository; keys will not be encrypted at rest.");
                            }
                        }
                    }
                    else
                    {
                        // Use profile isn't available - can we use the HKLM registry?
                        RegistryKey regKeyStorageKey = null;
                        if (OSVersionUtil.IsWindows())
                        {
                            regKeyStorageKey = RegistryXmlRepository.DefaultRegistryKey;
                        }
                        if (regKeyStorageKey != null)
                        {
                            // If the user profile isn't available, we can protect using DPAPI (to machine).
                            keyEncryptorDescriptor = DataProtectionServiceDescriptors.IXmlEncryptor_Dpapi(protectToMachine: true);
                            keyRepositoryDescriptor = DataProtectionServiceDescriptors.IXmlRepository_Registry(regKeyStorageKey);

                            if (log.IsInformationLevelEnabled())
                            {
                                log.LogInformationF($"User profile not available. Using '{regKeyStorageKey.Name}' as key repository and Windows DPAPI to encrypt keys at rest.");
                            }
                        }
                        else
                        {
                            // Final fallback - use an ephemeral repository since we don't know where else to go.
                            // This can only be used for development scenarios.
                            keyRepositoryDescriptor = DataProtectionServiceDescriptors.IXmlRepository_InMemory();

                            if (log.IsWarningLevelEnabled())
                            {
                                log.LogWarning("Neither user profile nor HKLM registry available. Using an ephemeral key repository. Protected data will be unavailable when application exits.");
                            }
                        }
                    }
                }

                return new DefaultKeyServices(
                    services: serviceProvider,
                    keyEncryptorDescriptor: keyEncryptorDescriptor,
                    keyRepositoryDescriptor: keyRepositoryDescriptor);
            }));

            // Provide root key management and data protection services
            services.TryAdd(DataProtectionServiceDescriptors.IKeyManager_Default());
            services.TryAdd(DataProtectionServiceDescriptors.IDataProtectionProvider_Default());

            // Provide services required for XML encryption
#if !DNXCORE50 // [[ISSUE60]] Remove this #ifdef when Core CLR gets support for EncryptedXml
            services.TryAdd(DataProtectionServiceDescriptors.ICertificateResolver_Default());
#endif

            // Hook up the logic which allows populating default options
            services.TryAdd(DataProtectionServiceDescriptors.ConfigureOptions_DataProtectionOptions());

            // Read and apply policy from the registry, overriding any other defaults.
            bool encryptorConfigurationReadFromRegistry = false;
            if (OSVersionUtil.IsWindows())
            {
                foreach (var descriptor in RegistryPolicyResolver.ResolveDefaultPolicy())
                {
                    services.TryAdd(descriptor);

                    if (descriptor.ServiceType == typeof(IAuthenticatedEncryptorConfiguration))
                    {
                        encryptorConfigurationReadFromRegistry = true;
                    }
                }
            }

            // Finally, provide a fallback encryptor configuration if one wasn't already specified.
            if (!encryptorConfigurationReadFromRegistry)
            {
                services.TryAdd(DataProtectionServiceDescriptors.IAuthenticatedEncryptorConfiguration_Default());
            }

            return services;
        }

        /// <summary>
        /// Configures the behavior of the Data Protection system.
        /// </summary>
        /// <param name="services">A service collection to which Data Protection has already been added.</param>
        /// <param name="configure">A callback which takes a <see cref="DataProtectionConfiguration"/> parameter.
        /// This callback will be responsible for configuring the system.</param>
        /// <returns>The <paramref name="services"/> instance.</returns>
        public static IServiceCollection ConfigureDataProtection([NotNull] this IServiceCollection services, [NotNull] Action<DataProtectionConfiguration> configure)
        {
            configure(new DataProtectionConfiguration(services));
            return services;
        }
    }
}
