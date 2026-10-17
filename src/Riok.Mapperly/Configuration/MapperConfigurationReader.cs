using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Riok.Mapperly.Abstractions;
using Riok.Mapperly.Configuration.MethodReferences;
using Riok.Mapperly.Descriptors;
using Riok.Mapperly.Descriptors.Mappings;
using Riok.Mapperly.Descriptors.Mappings.UserMappings;
using Riok.Mapperly.Diagnostics;
using Riok.Mapperly.Helpers;

namespace Riok.Mapperly.Configuration;

public class MapperConfigurationReader
{
    private readonly Dictionary<MappingConfigurationReference, MappingConfiguration> _resolvedConfigurations = new();
    private readonly Dictionary<INamedTypeSymbol, MappingConfiguration> _mapperConfigurations = new(SymbolEqualityComparer.Default);
    private readonly AttributeDataAccessor _dataAccessor;
    private readonly MappingCollection _mappings;
    private readonly GenericTypeChecker _genericTypeChecker;
    private readonly DiagnosticCollection _diagnostics;
    private readonly WellKnownTypes _types;
    private readonly ISymbol _mapperSymbol;
    private readonly MapperConfiguration _defaultMapperConfiguration;
    private readonly SupportedFeatures _supportedFeatures;

    public MapperConfigurationReader(
        AttributeDataAccessor dataAccessor,
        MappingCollection mappings,
        GenericTypeChecker genericTypeChecker,
        DiagnosticCollection diagnostics,
        WellKnownTypes types,
        ISymbol mapperSymbol,
        MapperConfiguration defaultMapperConfiguration,
        SupportedFeatures supportedFeatures
    )
    {
        _dataAccessor = dataAccessor;
        _mappings = mappings;
        _genericTypeChecker = genericTypeChecker;
        _diagnostics = diagnostics;
        _types = types;
        _mapperSymbol = mapperSymbol;
        _defaultMapperConfiguration = defaultMapperConfiguration;
        _supportedFeatures = supportedFeatures;

        MapperConfiguration = BuildMapperConfiguration(mapperSymbol, defaultMapperConfiguration);
    }

    public MappingConfiguration MapperConfiguration { get; }

    public MappingConfiguration BuildFor(MappingConfigurationReference reference, bool supportsDeepCloning)
    {
        if (_resolvedConfigurations.TryGetValue(reference, out var resolved))
        {
            return resolved;
        }

        return BuildWithIncludedMappings([], reference, supportsDeepCloning);
    }

    private MappingConfiguration BuildWithIncludedMappings(
        HashSet<IMethodSymbol> visitedMethods,
        MappingConfigurationReference reference,
        bool supportsDeepCloning
    )
    {
        if (reference.Method == null)
            return supportsDeepCloning ? MapperConfiguration : MapperConfiguration with { UseDeepCloning = false };

        var mapperConfiguration = MapperConfigurationFor(reference.Method);
        var enumConfig = BuildEnumConfig(reference, mapperConfiguration);
        var membersConfig = BuildMembersConfig(reference, mapperConfiguration);
        var derivedTypesConfig = BuildDerivedTypeConfigs(reference.Method);
        var configuration = new MappingConfiguration(
            mapperConfiguration.Mapper,
            enumConfig,
            membersConfig,
            derivedTypesConfig,
            supportsDeepCloning && mapperConfiguration.Mapper.UseDeepCloning,
            mapperConfiguration.StackCloningStrategy,
            mapperConfiguration.SupportedFeatures
        );

        configuration = IncludeConfigurations(configuration, visitedMethods, reference, supportsDeepCloning);
        _resolvedConfigurations[reference] = configuration;
        return configuration;
    }

    private MappingConfiguration BuildMapperConfiguration(ISymbol mapperSymbol, MapperConfiguration defaultMapperConfiguration)
    {
        var mapperConfiguration = _dataAccessor.AccessSingle<MapperAttribute, MapperConfiguration>(mapperSymbol);
        var mapper = MapperConfigurationMerger.MergeToAttribute(mapperConfiguration, defaultMapperConfiguration);

        return new MappingConfiguration(
            mapper,
            new EnumMappingConfiguration(
                mapper.EnumMappingStrategy,
                mapper.EnumMappingIgnoreCase,
                null,
                [],
                [],
                [],
                mapper.RequiredEnumMappingStrategy,
                mapper.EnumNamingStrategy
            ),
            new MembersMappingConfiguration([], [], [], [], [], mapper.IgnoreObsoleteMembersStrategy, mapper.RequiredMappingStrategy),
            [],
            mapper.UseDeepCloning,
            mapper.StackCloningStrategy,
            _supportedFeatures
        );
    }

    /// <summary>
    /// Resolves the mapper configuration which applies to the given mapping method.
    /// A method declared in another mapper (e.g. an external mapping rebuilt in a queryable projection)
    /// uses the effective configuration of its own mapper
    /// (its <see cref="MapperAttribute"/> merged with the <see cref="MapperDefaultsAttribute"/> of its assembly).
    /// </summary>
    private MappingConfiguration MapperConfigurationFor(IMethodSymbol method)
    {
        var mapper = method.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(mapper, _mapperSymbol) || !_dataAccessor.HasAttribute<MapperAttribute>(mapper))
            return MapperConfiguration;

        if (_mapperConfigurations.TryGetValue(mapper, out var configuration))
            return configuration;

        // the MSBuild defaults of other assemblies are unknown,
        // only their MapperDefaultsAttribute is considered
        var defaultConfiguration = SymbolEqualityComparer.Default.Equals(mapper.ContainingAssembly, _mapperSymbol.ContainingAssembly)
            ? _defaultMapperConfiguration
            : _dataAccessor
                .TryAccess<MapperDefaultsAttribute, MapperConfiguration>(mapper.ContainingAssembly.GetAttributes())
                .FirstOrDefault()
                ?? new MapperConfiguration();
        configuration = BuildMapperConfiguration(mapper, defaultConfiguration);
        _mapperConfigurations[mapper] = configuration;
        return configuration;
    }

    private MappingConfiguration IncludeConfigurations(
        MappingConfiguration configuration,
        HashSet<IMethodSymbol> visitedMethods,
        MappingConfigurationReference reference,
        bool supportsDeepCloning
    )
    {
        var includedMappingConfigs = _dataAccessor.Access<IncludeMappingConfigurationAttribute, IncludeMappingConfiguration>(
            reference.Method!
        );
        foreach (var includedCfg in includedMappingConfigs)
        {
            var cfgToInclude = BuildConfigToInclude(includedCfg.Name, reference, visitedMethods, supportsDeepCloning);
            if (cfgToInclude == null)
            {
                continue;
            }

            configuration = configuration.Include(cfgToInclude);
        }

        return configuration;
    }

    private MappingConfiguration? BuildConfigToInclude(
        IMethodReferenceConfiguration name,
        MappingConfigurationReference configRef,
        HashSet<IMethodSymbol> visitedMethods,
        bool supportsDeepCloning
    )
    {
        var typeMapping =
            (ITypeMapping?)_mappings.FindNamedNewInstanceMapping(name.FullName, out var ambiguousName)
            ?? _mappings.FindExistingInstanceNamedMapping(name.FullName, out ambiguousName);
        var methodSymbol = (typeMapping as IUserMapping)?.Method;
        if (!ValidateIncludedConfig(ambiguousName, configRef, name.FullName, typeMapping, methodSymbol))
        {
            return null;
        }

        if (!visitedMethods.Add(methodSymbol))
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.CircularReferencedMapping, methodSymbol, methodSymbol.ToDisplayString());
            return null;
        }

        var includedReference = new MappingConfigurationReference(methodSymbol, typeMapping.SourceType, typeMapping.TargetType);
        if (!_resolvedConfigurations.TryGetValue(includedReference, out var config))
        {
            config = BuildWithIncludedMappings(visitedMethods, includedReference, supportsDeepCloning);
        }

        return config;
    }

    private bool ValidateIncludedConfig(
        bool ambiguousName,
        MappingConfigurationReference configRef,
        string mappingToIncludeName,
        [NotNullWhen(true)] ITypeMapping? mappingToInclude,
        [NotNullWhen(true)] IMethodSymbol? mappingToIncludeMethodSymbol
    )
    {
        if (ambiguousName)
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.ReferencedMappingAmbiguous, configRef.Method, mappingToIncludeName);
            return false;
        }

        if (mappingToInclude is null || mappingToIncludeMethodSymbol is null)
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.ReferencedMappingNotFound, configRef.Method, mappingToIncludeName);
            return false;
        }

        var typeCheckerResult = _genericTypeChecker.InferAndCheckTypes(
            configRef.Method!.TypeParameters,
            (mappingToInclude.SourceType, configRef.Source),
            (mappingToInclude.TargetType, configRef.Target)
        );

        if (typeCheckerResult.Success)
            return true;

        if (ReferenceEquals(configRef.Source, typeCheckerResult.FailedArgument))
        {
            _diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.SourceTypeIsNotAssignableToTheIncludedSourceType,
                configRef.Method,
                configRef.Source,
                mappingToInclude.SourceType
            );
        }
        else
        {
            _diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.TargetTypeIsNotAssignableToTheIncludedTargetType,
                configRef.Method,
                configRef.Target,
                mappingToInclude.TargetType
            );
        }

        return false;
    }

    private IReadOnlyCollection<DerivedTypeMappingConfiguration> BuildDerivedTypeConfigs(IMethodSymbol method)
    {
        return _dataAccessor
            .Access<MapDerivedTypeAttribute, DerivedTypeMappingConfiguration>(method)
            .Concat(_dataAccessor.Access<MapDerivedTypeAttribute<object, object>, DerivedTypeMappingConfiguration>(method))
            .ToList();
    }

    private MembersMappingConfiguration BuildMembersConfig(
        MappingConfigurationReference configRef,
        MappingConfiguration mapperConfiguration
    )
    {
        if (configRef.Method == null)
            return mapperConfiguration.Members;

        var ignoreSourceMemberAttributes = _dataAccessor
            .Access<MapperIgnoreSourceAttribute, MapperIgnoreMemberConfiguration>(configRef.Method)
            .ToList();
        var ignoredSourceMembers = ignoreSourceMemberAttributes.Select(x => x.Value).WhereNotNull().ToList();
        var ignoreTargetMemberAttributes = _dataAccessor
            .Access<MapperIgnoreTargetAttribute, MapperIgnoreMemberConfiguration>(configRef.Method)
            .ToList();
        var ignoredTargetMembers = ignoreTargetMemberAttributes.Select(x => x.Value).WhereNotNull().ToList();
        var memberValueConfigurations = _dataAccessor.Access<MapValueAttribute, MemberValueMappingConfiguration>(configRef.Method).ToList();
        var memberConfigurations = _dataAccessor
            .Access<MapPropertyAttribute, MemberMappingConfiguration>(configRef.Method)
            .Concat(_dataAccessor.Access<MapPropertyFromSourceAttribute, MemberMappingConfiguration>(configRef.Method))
            .ToList();
        var nestedMembersConfigurations = _dataAccessor
            .Access<MapNestedPropertiesAttribute, NestedMembersMappingConfiguration>(configRef.Method)
            .ToList();
        var ignoreObsolete = _dataAccessor
            .AccessFirstOrDefault<MapperIgnoreObsoleteMembersAttribute>(configRef.Method)
            ?.IgnoreObsoleteStrategy;
        var requiredMapping = _dataAccessor.AccessFirstOrDefault<MapperRequiredMappingAttribute>(configRef.Method)?.RequiredMappingStrategy;

        // ignore the required mapping / ignore obsolete as the same attribute is used for other mapping types
        // e.g. enum to enum
        var hasMemberConfigs = ignoredSourceMembers.Count > 0 || ignoredTargetMembers.Count > 0 || memberConfigurations.Count > 0;
        if (hasMemberConfigs && (configRef.Source.IsEnum() || configRef.Target.IsEnum()))
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.MemberConfigurationOnNonMemberMapping, configRef.Method);
            return mapperConfiguration.Members;
        }

        if (
            hasMemberConfigs
            && configRef.Source.ImplementsGeneric(_types.Get(typeof(IQueryable<>)), out _)
            && configRef.Target.ImplementsGeneric(_types.Get(typeof(IQueryable<>)), out _)
        )
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.MemberConfigurationOnQueryableProjectionMapping, configRef.Method);
            return mapperConfiguration.Members;
        }

        ReportMissingJustificationDiagnostics(ignoreSourceMemberAttributes, static x => x.Value);
        ReportMissingJustificationDiagnostics(ignoreTargetMemberAttributes, static x => x.Value);

        foreach (var invalidMemberConfig in memberValueConfigurations.Where(x => !x.IsValid))
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.InvalidMapValueAttributeUsage, invalidMemberConfig.Location);
        }

        foreach (var invalidMemberConfig in memberConfigurations.Where(x => !x.IsValid))
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.InvalidMapPropertyAttributeUsage, invalidMemberConfig.Location);
        }

        return new MembersMappingConfiguration(
            ignoredSourceMembers,
            ignoredTargetMembers,
            memberValueConfigurations,
            memberConfigurations,
            nestedMembersConfigurations,
            ignoreObsolete,
            requiredMapping
        );
    }

    private EnumMappingConfiguration BuildEnumConfig(MappingConfigurationReference configRef, MappingConfiguration mapperConfiguration)
    {
        if (configRef.Method == null)
            return mapperConfiguration.Enum;

        var configData = _dataAccessor.AccessFirstOrDefault<MapEnumAttribute, EnumConfiguration>(configRef.Method);
        var explicitMappings = _dataAccessor.Access<MapEnumValueAttribute, EnumValueMappingConfiguration>(configRef.Method).ToList();
        var ignoredSourceValueConfigurations = _dataAccessor
            .Access<MapperIgnoreSourceValueAttribute, MapperIgnoreEnumValueConfiguration>(configRef.Method)
            .ToList();
        var ignoredSources = ignoredSourceValueConfigurations.Select(x => x.Value).ToList();
        var ignoredTargetValueConfigurations = _dataAccessor
            .Access<MapperIgnoreTargetValueAttribute, MapperIgnoreEnumValueConfiguration>(configRef.Method)
            .ToList();
        var ignoredTargets = ignoredTargetValueConfigurations.Select(x => x.Value).ToList();
        var requiredMapping = _dataAccessor.AccessFirstOrDefault<MapperRequiredMappingAttribute>(configRef.Method)?.RequiredMappingStrategy;

        ReportMissingJustificationDiagnostics(ignoredSourceValueConfigurations, static x => x.Value.Name);
        ReportMissingJustificationDiagnostics(ignoredTargetValueConfigurations, static x => x.Value.Name);

        // ignore the required mapping as the same attribute is used for other mapping types
        // e.g. object to object
        var hasEnumConfigs = configData != null || explicitMappings.Count > 0 || ignoredSources.Count > 0 || ignoredTargets.Count > 0;
        if (hasEnumConfigs && !configRef.Source.IsEnum() && !configRef.Target.IsEnum())
        {
            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.EnumConfigurationOnNonEnumMapping, configRef.Method);
            return mapperConfiguration.Enum;
        }

        return new EnumMappingConfiguration(
            configData?.Strategy ?? mapperConfiguration.Enum.Strategy,
            configData?.IgnoreCase ?? mapperConfiguration.Enum.IgnoreCase,
            configData?.FallbackValue,
            ignoredSources,
            ignoredTargets,
            explicitMappings,
            requiredMapping ?? mapperConfiguration.Enum.RequiredMappingStrategy,
            configData?.NamingStrategy ?? mapperConfiguration.Enum.NamingStrategy
        );
    }

    private void ReportMissingJustificationDiagnostics<TConfiguration>(
        IEnumerable<TConfiguration> ignoreConfigurations,
        Func<TConfiguration, string?> ignoredNameAccessor
    )
        where TConfiguration : MapperIgnoreConfigurationBase
    {
        foreach (var ignoreConfiguration in ignoreConfigurations)
        {
            if (!string.IsNullOrWhiteSpace(ignoreConfiguration.Justification))
                continue;

            var ignoredName = ignoredNameAccessor(ignoreConfiguration);
            if (ignoredName == null)
                continue;

            _diagnostics.ReportDiagnostic(DiagnosticDescriptors.IgnoreMissingJustification, ignoreConfiguration.Location, ignoredName);
        }
    }
}
