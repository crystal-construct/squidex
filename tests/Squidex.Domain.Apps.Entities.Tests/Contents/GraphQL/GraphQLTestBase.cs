﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using FakeItEasy;
using GraphQL;
using GraphQL.DataLoader;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NodaTime;
using Squidex.Domain.Apps.Core;
using Squidex.Domain.Apps.Core.Contents;
using Squidex.Domain.Apps.Core.Schemas;
using Squidex.Domain.Apps.Entities.Apps;
using Squidex.Domain.Apps.Entities.Assets;
using Squidex.Domain.Apps.Entities.Contents.TestData;
using Squidex.Domain.Apps.Entities.Schemas;
using Squidex.Domain.Apps.Entities.TestHelpers;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Json;
using Squidex.Infrastructure.Json.Objects;
using Squidex.Infrastructure.Log;
using Xunit;

#pragma warning disable SA1311 // Static readonly fields must begin with upper-case letter
#pragma warning disable SA1401 // Fields must be private

namespace Squidex.Domain.Apps.Entities.Contents.GraphQL
{
    public class GraphQLTestBase
    {
        protected readonly IAppEntity app;
        protected readonly IAssetQueryService assetQuery = A.Fake<IAssetQueryService>();
        protected readonly IContentQueryService contentQuery = A.Fake<IContentQueryService>();
        protected readonly IDependencyResolver dependencyResolver;
        protected readonly IJsonSerializer serializer = TestUtils.CreateSerializer(TypeNameHandling.None);
        protected readonly ISchemaEntity schema;
        protected readonly Context requestContext;
        protected readonly NamedId<Guid> appId = NamedId.Of(Guid.NewGuid(), "my-app");
        protected readonly NamedId<Guid> schemaId = NamedId.Of(Guid.NewGuid(), "my-schema");
        protected readonly IGraphQLService sut;

        public GraphQLTestBase()
        {
            app = Mocks.App(appId, Language.DE, Language.GermanGermany);

            var schemaDef =
                new Schema("my-schema")
                    .AddJson(1, "my-json", Partitioning.Invariant,
                        new JsonFieldProperties())
                    .AddString(2, "my-string", Partitioning.Language,
                        new StringFieldProperties())
                    .AddNumber(3, "my-number", Partitioning.Invariant,
                        new NumberFieldProperties())
                    .AddNumber(4, "my_number", Partitioning.Invariant,
                        new NumberFieldProperties())
                    .AddAssets(5, "my-assets", Partitioning.Invariant,
                        new AssetsFieldProperties())
                    .AddBoolean(6, "my-boolean", Partitioning.Invariant,
                        new BooleanFieldProperties())
                    .AddDateTime(7, "my-datetime", Partitioning.Invariant,
                        new DateTimeFieldProperties())
                    .AddReferences(8, "my-references", Partitioning.Invariant,
                        new ReferencesFieldProperties { SchemaId = schemaId.Id })
                    .AddReferences(9, "my-invalid", Partitioning.Invariant,
                        new ReferencesFieldProperties { SchemaId = Guid.NewGuid() })
                    .AddGeolocation(10, "my-geolocation", Partitioning.Invariant,
                        new GeolocationFieldProperties())
                    .AddTags(11, "my-tags", Partitioning.Invariant,
                        new TagsFieldProperties())
                    .AddString(12, "my-localized", Partitioning.Language,
                        new StringFieldProperties())
                    .AddArray(13, "my-array", Partitioning.Invariant, f => f
                        .AddBoolean(121, "nested-boolean")
                        .AddNumber(122, "nested-number")
                        .AddNumber(123, "nested_number"))
                    .ConfigureScripts(new SchemaScripts { Query = "<query-script>" })
                    .Publish();

            schema = Mocks.Schema(appId, schemaId, schemaDef);

            requestContext = new Context(Mocks.FrontendUser(), app);

            sut = CreateSut();
        }

        protected static IEnrichedContentEntity CreateContent(Guid id, Guid refId, Guid assetId, NamedContentData data = null, NamedContentData dataDraft = null)
        {
            var now = SystemClock.Instance.GetCurrentInstant();

            data = data ??
                new NamedContentData()
                    .AddField("my-string",
                        new ContentFieldData()
                            .AddValue("de", "value"))
                    .AddField("my-assets",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Array(assetId.ToString())))
                    .AddField("my-number",
                        new ContentFieldData()
                            .AddValue("iv", 1.0))
                    .AddField("my_number",
                        new ContentFieldData()
                            .AddValue("iv", 2.0))
                    .AddField("my-boolean",
                        new ContentFieldData()
                            .AddValue("iv", true))
                    .AddField("my-datetime",
                        new ContentFieldData()
                            .AddValue("iv", now))
                    .AddField("my-tags",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Array("tag1", "tag2")))
                    .AddField("my-references",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Array(refId.ToString())))
                    .AddField("my-geolocation",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Object().Add("latitude", 10).Add("longitude", 20)))
                    .AddField("my-json",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Object().Add("value", 1)))
                    .AddField("my-localized",
                        new ContentFieldData()
                            .AddValue("de-DE", "de-DE"))
                    .AddField("my-array",
                        new ContentFieldData()
                            .AddValue("iv", JsonValue.Array(
                                JsonValue.Object()
                                    .Add("nested-boolean", true)
                                    .Add("nested-number", 10)
                                    .Add("nested_number", 11),
                                JsonValue.Object()
                                    .Add("nested-boolean", false)
                                    .Add("nested-number", 20)
                                    .Add("nested_number", 21))));

            var content = new ContentEntity
            {
                Id = id,
                Version = 1,
                Created = now,
                CreatedBy = new RefToken(RefTokenType.Subject, "user1"),
                LastModified = now,
                LastModifiedBy = new RefToken(RefTokenType.Subject, "user2"),
                Data = data,
                DataDraft = dataDraft,
                Status = Status.Draft,
                StatusColor = "red"
            };

            return content;
        }

        protected static IEnrichedAssetEntity CreateAsset(Guid id)
        {
            var now = SystemClock.Instance.GetCurrentInstant();

            var asset = new AssetEntity
            {
                Id = id,
                Version = 1,
                Created = now,
                CreatedBy = new RefToken(RefTokenType.Subject, "user1"),
                LastModified = now,
                LastModifiedBy = new RefToken(RefTokenType.Subject, "user2"),
                FileName = "MyFile.png",
                Slug = "myfile.png",
                FileSize = 1024,
                FileHash = "ABC123",
                FileVersion = 123,
                MimeType = "image/png",
                IsImage = true,
                PixelWidth = 800,
                PixelHeight = 600,
                TagNames = new[] { "tag1", "tag2" }.ToHashSet()
            };

            return asset;
        }

        protected void AssertResult(object expected, (bool HasErrors, object Response) result, bool checkErrors = true)
        {
            if (checkErrors && result.HasErrors)
            {
                throw new InvalidOperationException(Serialize(result));
            }

            var resultJson = serializer.Serialize(result.Response, true);
            var expectJson = serializer.Serialize(expected, true);

            Assert.Equal(expectJson, resultJson);
        }

        private string Serialize((bool HasErrors, object Response) result)
        {
            return serializer.Serialize(result);
        }

        private CachingGraphQLService CreateSut()
        {
            var appProvider = A.Fake<IAppProvider>();

            A.CallTo(() => appProvider.GetSchemasAsync(appId.Id))
                .Returns(new List<ISchemaEntity> { schema });

            var dataLoaderContext = new DataLoaderContextAccessor();

            var services = new Dictionary<Type, object>
            {
                [typeof(IAppProvider)] = appProvider,
                [typeof(IAssetQueryService)] = assetQuery,
                [typeof(IContentQueryService)] = contentQuery,
                [typeof(IDataLoaderContextAccessor)] = dataLoaderContext,
                [typeof(IGraphQLUrlGenerator)] = new FakeUrlGenerator(),
                [typeof(ISemanticLog)] = A.Fake<ISemanticLog>(),
                [typeof(DataLoaderDocumentListener)] = new DataLoaderDocumentListener(dataLoaderContext)
            };

            var resolver = new FuncDependencyResolver(t => services[t]);

            var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

            return new CachingGraphQLService(cache, resolver);
        }
    }
}
