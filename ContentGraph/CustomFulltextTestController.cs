using System.Text.Json;
using Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to create and verify test blocks that cover all PropertyIndexingMode combinations.
    /// Tests the following cases against _fulltext behavior:
    ///   Case 1: [GraphProperty(OutputOnly)]               → NOT in _fulltext
    ///   Case 2: [GraphProperty(Default)]                  → NOT in _fulltext (confirmed by PropertyUtil.cs)
    ///   Case 3: No [GraphProperty] at all                 → IN _fulltext (CMS Searchable default for string/XhtmlString)
    ///   Case 4: [Searchable(true)] only                   → IN _fulltext
    ///   Case 5: [GraphProperty(Default)] + [Searchable(true)] → NOT in _fulltext (GraphProperty takes precedence)
    /// </summary>
    [ApiController]
    [Route("util-api/custom-fulltext-test")]
    public class CustomFulltextTestController : ControllerBase
    {
        private readonly IContentRepository _contentRepository;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IClient _graphClient;
        private readonly IOptions<QueryOptions> _queryOptions;

        private const string TestFolderName = "[Test] Fulltext Cases";
        private const string TestPrefix = "FulltextTest_";

        public CustomFulltextTestController(
            IContentRepository contentRepository,
            IContentTypeRepository contentTypeRepository,
            IClient graphClient,
            IOptions<QueryOptions> queryOptions)
        {
            _contentRepository = contentRepository;
            _contentTypeRepository = contentTypeRepository;
            _graphClient = graphClient;
            _queryOptions = queryOptions;
        }

        /// <summary>
        /// Step 1: Show all test cases and sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-fulltext-test/overview
        /// </summary>
        [HttpGet("overview")]
        public IActionResult Overview()
        {
            try
            {
                return Ok(new
                {
                    Step = "1 - Overview",
                    Description = "Shows the 5 test cases for PropertyIndexingMode vs _fulltext behavior. Source: PropertyUtil.cs lines 68-72 — both OutputOnly AND Default return false from IsSearchable().",
                    SourceCodeEvidence = new
                    {
                        File = "PropertyUtil.cs",
                        Lines = "68-72",
                        Code = "if (indexingMode is PropertyIndexingMode.OutputOnly or PropertyIndexingMode.Default) { return false; }",
                        Implication = "BOTH OutputOnly and Default make properties NOT searchable — neither will appear in _fulltext."
                    },
                    TestCases = new[]
                    {
                        new { Case = 1, BlockType = "FulltextTestBlockOutputOnly",         Attributes = "[GraphProperty(OutputOnly)]",                        ExpectedInFulltext = false, Reason = "OutputOnly → IsSearchable returns false" },
                        new { Case = 2, BlockType = "FulltextTestBlockDefault",             Attributes = "[GraphProperty(Default)]",                           ExpectedInFulltext = false, Reason = "Default → IsSearchable returns false" },
                        new { Case = 3, BlockType = "FulltextTestBlockNoAttribute",         Attributes = "(none)",                                             ExpectedInFulltext = true,  Reason = "No GraphProperty → falls through to CMS Searchable (true for string/XhtmlString)" },
                        new { Case = 4, BlockType = "FulltextTestBlockSearchableTrue",      Attributes = "[Searchable(true)]",                                 ExpectedInFulltext = true,  Reason = "No GraphProperty + explicit Searchable(true) → searchable" },
                        new { Case = 5, BlockType = "FulltextTestBlockDefaultWithSearchable",Attributes = "[GraphProperty(Default)] + [Searchable(true)]",      ExpectedInFulltext = false, Reason = "Default mode checked BEFORE Searchable → not searchable" }
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-fulltext-test/overview",
                        "https://localhost:5009/util-api/custom-fulltext-test/create-test-content",
                        "https://localhost:5009/util-api/custom-fulltext-test/find-test-content",
                        "https://localhost:5009/util-api/custom-fulltext-test/verify-from-graph",
                        "https://localhost:5009/util-api/custom-fulltext-test/verify-all"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Creates one block instance for each test case under the Global Assets folder.
        /// Each block has a unique Heading and Body so we can verify them in _fulltext.
        /// Sample usage: https://localhost:5009/util-api/custom-fulltext-test/create-test-content
        /// </summary>
        [HttpGet("create-test-content")]
        public IActionResult CreateTestContent()
        {
            try
            {
                var globalAssets = ContentReference.GlobalBlockFolder;
                var results = new List<object>();

                // Case 1: OutputOnly
                results.Add(CreateTestBlock<FulltextTestBlockOutputOnly>(
                    globalAssets, "Case1_OutputOnly",
                    "Unicorn OutputOnly Heading", "<p>Pegasus OutputOnly Body Content</p>"));

                // Case 2: Default
                results.Add(CreateTestBlock<FulltextTestBlockDefault>(
                    globalAssets, "Case2_Default",
                    "Unicorn Default Heading", "<p>Pegasus Default Body Content</p>"));

                // Case 3: No Attribute
                results.Add(CreateTestBlock<FulltextTestBlockNoAttribute>(
                    globalAssets, "Case3_NoAttribute",
                    "Unicorn NoAttribute Heading", "<p>Pegasus NoAttribute Body Content</p>"));

                // Case 4: Searchable True
                results.Add(CreateTestBlock<FulltextTestBlockSearchableTrue>(
                    globalAssets, "Case4_SearchableTrue",
                    "Unicorn SearchableTrue Heading", "<p>Pegasus SearchableTrue Body Content</p>"));

                // Case 5: Default + Searchable
                results.Add(CreateTestBlock<FulltextTestBlockDefaultWithSearchable>(
                    globalAssets, "Case5_DefaultWithSearchable",
                    "Unicorn DefaultSearchable Heading", "<p>Pegasus DefaultSearchable Body Content</p>"));

                return Ok(new
                {
                    Step = "2 - Create Test Content",
                    Description = "Created 5 test blocks. Each has unique Heading/Body containing 'Unicorn' and 'Pegasus' keywords for easy Graph verification. Trigger a Graph sync from CMS Admin, then use Step 4 to verify.",
                    NextStep = "After Graph sync: https://localhost:5009/util-api/custom-fulltext-test/verify-from-graph",
                    Blocks = results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Find existing test blocks in the CMS.
        /// Sample usage: https://localhost:5009/util-api/custom-fulltext-test/find-test-content
        /// </summary>
        [HttpGet("find-test-content")]
        public IActionResult FindTestContent()
        {
            try
            {
                var blocks = FindTestBlocks();

                return Ok(new
                {
                    Step = "3 - Find Test Content",
                    Description = "Lists test blocks found in CMS. Use their IDs in Step 4.",
                    TotalFound = blocks.Count,
                    Blocks = blocks
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Query Content Graph to verify _fulltext for each test block type.
        /// Searches for the 'Unicorn' keyword in _fulltext to see which cases are searchable.
        /// Sample usage: https://localhost:5009/util-api/custom-fulltext-test/verify-from-graph
        /// </summary>
        [HttpGet("verify-from-graph")]
        public async Task<IActionResult> VerifyFromGraph()
        {
            try
            {
                var testTypes = new[]
                {
                    new { Case = 1, TypeName = "FulltextTestBlockOutputOnly",          ExpectedInFulltext = false },
                    new { Case = 2, TypeName = "FulltextTestBlockDefault",              ExpectedInFulltext = false },
                    new { Case = 3, TypeName = "FulltextTestBlockNoAttribute",          ExpectedInFulltext = true },
                    new { Case = 4, TypeName = "FulltextTestBlockSearchableTrue",       ExpectedInFulltext = true },
                    new { Case = 5, TypeName = "FulltextTestBlockDefaultWithSearchable",ExpectedInFulltext = false }
                };

                var results = new List<object>();

                foreach (var testType in testTypes)
                {
                    // Query 1: Get all items of this type (with _fulltext)
                    var allQuery = $@"{{ {testType.TypeName}(limit: 5) {{ items {{ Name _fulltext Heading ContentLink {{ Id }} }} total }} }}";
                    var allResponse = await _graphClient.QueryAsync(allQuery, new { });

                    // Query 2: Search _fulltext for our unique keyword
                    var searchQuery = $@"{{ {testType.TypeName}(limit: 5, where: {{ _fulltext: {{ match: ""Unicorn"" }} }}) {{ items {{ Name _fulltext Heading ContentLink {{ Id }} }} total }} }}";
                    string searchResponse;
                    try
                    {
                        searchResponse = await _graphClient.QueryAsync(searchQuery, new { });
                    }
                    catch (Exception searchEx)
                    {
                        searchResponse = JsonSerializer.Serialize(new { error = searchEx.Message });
                    }

                    var allParsed = TryParseJson(allResponse);
                    var searchParsed = TryParseJson(searchResponse);

                    results.Add(new
                    {
                        testType.Case,
                        testType.TypeName,
                        testType.ExpectedInFulltext,
                        AllItems = allParsed,
                        SearchByFulltext = searchParsed
                    });
                }

                return Ok(new
                {
                    Step = "4 - Verify from Graph",
                    Description = "For each test block type: (1) fetches all items showing _fulltext content, (2) searches _fulltext for 'Unicorn'. If _fulltext search returns 0 results, the property is NOT searchable.",
                    HowToRead = "Compare 'AllItems' (should show Heading value) with 'SearchByFulltext' (whether searching that value via _fulltext works). If search returns 0 but AllItems shows data, the property is stored but NOT indexed for fulltext.",
                    Results = results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: All-in-one verification that inspects CMS-side attributes AND Graph-side _fulltext.
        /// Shows a side-by-side comparison of expected vs actual behavior for all 5 test cases.
        /// Sample usage: https://localhost:5009/util-api/custom-fulltext-test/verify-all
        /// </summary>
        [HttpGet("verify-all")]
        public async Task<IActionResult> VerifyAll()
        {
            try
            {
                var cmsBlocks = FindTestBlocks();
                var testCases = new[]
                {
                    new { Case = 1, TypeName = "FulltextTestBlockOutputOnly",           Attributes = "[GraphProperty(OutputOnly)]",                    ExpectedInFulltext = false },
                    new { Case = 2, TypeName = "FulltextTestBlockDefault",               Attributes = "[GraphProperty(Default)]",                       ExpectedInFulltext = false },
                    new { Case = 3, TypeName = "FulltextTestBlockNoAttribute",           Attributes = "(none)",                                         ExpectedInFulltext = true },
                    new { Case = 4, TypeName = "FulltextTestBlockSearchableTrue",        Attributes = "[Searchable(true)]",                             ExpectedInFulltext = true },
                    new { Case = 5, TypeName = "FulltextTestBlockDefaultWithSearchable", Attributes = "[GraphProperty(Default)] + [Searchable(true)]",  ExpectedInFulltext = false }
                };

                var results = new List<object>();

                foreach (var tc in testCases)
                {
                    var cmsBlock = cmsBlocks.FirstOrDefault(b => b.ContentTypeName == tc.TypeName);

                    // Query Graph
                    object graphData = null;
                    bool? actualInFulltext = null;
                    try
                    {
                        var query = $@"{{ {tc.TypeName}(limit: 5, where: {{ _fulltext: {{ match: ""Unicorn"" }} }}) {{ total }} }}";
                        var response = await _graphClient.QueryAsync(query, new { });
                        var parsed = JsonDocument.Parse(response);
                        var total = parsed.RootElement
                            .GetProperty("data")
                            .GetProperty(tc.TypeName)
                            .GetProperty("total")
                            .GetInt32();
                        actualInFulltext = total > 0;
                        graphData = new { SearchTotal = total, FullQuery = query };
                    }
                    catch (Exception gex)
                    {
                        graphData = new { Error = gex.Message };
                    }

                    var passed = actualInFulltext.HasValue && actualInFulltext.Value == tc.ExpectedInFulltext;

                    results.Add(new
                    {
                        tc.Case,
                        tc.TypeName,
                        tc.Attributes,
                        tc.ExpectedInFulltext,
                        ActualInFulltext = actualInFulltext,
                        Passed = passed,
                        Status = !actualInFulltext.HasValue ? "⚠️ GRAPH ERROR" : passed ? "✅ PASS" : "❌ FAIL",
                        CmsBlock = cmsBlock != null ? new { cmsBlock.ContentId, cmsBlock.Name } : null,
                        Graph = graphData
                    });
                }

                var allPassed = results.Cast<dynamic>().All(r => r.Passed == true);

                return Ok(new
                {
                    Step = "5 - Verify All",
                    Description = "Side-by-side comparison of expected vs actual _fulltext behavior for all 5 test cases.",
                    OverallResult = allPassed ? "✅ ALL TESTS PASSED" : "❌ SOME TESTS FAILED — see individual results",
                    Conclusion = new
                    {
                        OutputOnly = "NOT in _fulltext (property excluded entirely from search index)",
                        Default = "NOT in _fulltext (property is filterable/queryable but NOT searchable)",
                        NoAttribute = "IN _fulltext (CMS defaults string/XhtmlString to Searchable=true)",
                        SearchableTrue = "IN _fulltext (explicit [Searchable(true)] works when no GraphProperty)",
                        DefaultPlusSearchable = "NOT in _fulltext (GraphProperty.Default checked BEFORE Searchable — GraphProperty wins)"
                    },
                    Results = results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Private Helpers

        private object CreateTestBlock<T>(ContentReference parent, string suffix, string heading, string body) where T : BlockData
        {
            var name = $"{TestPrefix}{suffix}";

            // Check if already exists
            var existing = _contentRepository.GetChildren<T>(parent)
                .FirstOrDefault(b => (b as IContent)?.Name == name);

            if (existing != null)
            {
                var existingContent = existing as IContent;
                return new
                {
                    Status = "ALREADY_EXISTS",
                    ContentId = existingContent?.ContentLink?.ID,
                    Name = existingContent?.Name,
                    ContentTypeName = typeof(T).Name
                };
            }

            var block = _contentRepository.GetDefault<T>(parent);
            var content = block as IContent;
            content.Name = name;

            // Set properties via reflection since the types differ
            var headingProp = typeof(T).GetProperty("Heading");
            var bodyProp = typeof(T).GetProperty("Body");

            headingProp?.SetValue(block, heading);
            bodyProp?.SetValue(block, new XhtmlString(body));

            var saved = _contentRepository.Save(content, EPiServer.DataAccess.SaveAction.Publish, EPiServer.Security.AccessLevel.NoAccess);

            return new
            {
                Status = "CREATED",
                ContentId = saved.ID,
                Name = content.Name,
                ContentTypeName = typeof(T).Name,
                Heading = heading,
                Body = body
            };
        }

        private List<TestBlockInfo> FindTestBlocks()
        {
            var results = new List<TestBlockInfo>();
            var globalAssets = ContentReference.GlobalBlockFolder;

            void AddBlocks<T>(string typeName) where T : BlockData
            {
                foreach (var block in _contentRepository.GetChildren<T>(globalAssets))
                {
                    var content = block as IContent;
                    if (content?.Name?.StartsWith(TestPrefix) == true)
                    {
                        var headingProp = typeof(T).GetProperty("Heading");
                        var bodyProp = typeof(T).GetProperty("Body");
                        results.Add(new TestBlockInfo
                        {
                            ContentId = content.ContentLink.ID,
                            Name = content.Name,
                            ContentTypeName = typeName,
                            Heading = headingProp?.GetValue(block)?.ToString(),
                            Body = bodyProp?.GetValue(block)?.ToString()
                        });
                    }
                }
            }

            AddBlocks<FulltextTestBlockOutputOnly>("FulltextTestBlockOutputOnly");
            AddBlocks<FulltextTestBlockDefault>("FulltextTestBlockDefault");
            AddBlocks<FulltextTestBlockNoAttribute>("FulltextTestBlockNoAttribute");
            AddBlocks<FulltextTestBlockSearchableTrue>("FulltextTestBlockSearchableTrue");
            AddBlocks<FulltextTestBlockDefaultWithSearchable>("FulltextTestBlockDefaultWithSearchable");

            return results;
        }

        private static object TryParseJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
                return new { IsEmpty = true, Raw = rawResponse };

            try
            {
                using var document = JsonDocument.Parse(rawResponse);
                return document.RootElement.Clone();
            }
            catch
            {
                return new { IsJson = false, Raw = rawResponse };
            }
        }

        private class TestBlockInfo
        {
            public int ContentId { get; set; }
            public string Name { get; set; }
            public string ContentTypeName { get; set; }
            public string Heading { get; set; }
            public string Body { get; set; }
        }

        #endregion
    }
}
