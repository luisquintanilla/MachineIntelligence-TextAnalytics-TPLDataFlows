﻿using MachineIntelligenceTPLDataFlows.Classes;
using MachineIntelligenceTPLDataFlows.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SemanticFunctions;
using Newtonsoft.Json;
using Polly;
using Polly.Extensions.Http;
using Polly.Registry;
using SharpToken;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MachineIntelligenceTPLDataFlows

{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "Machine Intelligence (Text Analytics) with TPL Data Flows & OpenAI SQL Vector Embeddings";

            var aciiArt = """
                |\     /|(  ____ \(  ____ \\__   __/(  ___  )(  ____ )
                | )   ( || (    \/| (    \/   ) (   | (   ) || (    )|
                | |   | || (__    | |         | |   | |   | || (____)|
                ( (   ) )|  __)   | |         | |   | |   | ||     __)
                 \ \_/ / | (      | |         | |   | |   | || (\ (   
                  \   /  | (____/\| (____/\   | |   | (___) || ) \ \__
                   \_/   (_______/(_______/   )_(   (_______)|/   \__/

                 ______   _______ _________ _______  ______   _______  _______  _______ 
                (  __  \ (  ___  )\__   __/(  ___  )(  ___ \ (  ___  )(  ____ \(  ____ \
                | (  \  )| (   ) |   ) (   | (   ) || (   ) )| (   ) || (    \/| (    \/
                | |   ) || (___) |   | |   | (___) || (__/ / | (___) || (_____ | (__    
                | |   | ||  ___  |   | |   |  ___  ||  __ (  |  ___  |(_____  )|  __)   
                | |   ) || (   ) |   | |   | (   ) || (  \ \ | (   ) |      ) || (      
                | (__/  )| )   ( |   | |   | )   ( || )___) )| )   ( |/\____) || (____/\
                (______/ |/     \|   )_(   |/     \||/ \___/ |/     \|\_______)(_______/

                 _______  _______  _          _______  _______  _______  _______  _______          
                (  ____ \(  ___  )( \        (  ____ \(  ____ \(  ___  )(  ____ )(  ____ \|\     /|
                | (    \/| (   ) || (        | (    \/| (    \/| (   ) || (    )|| (    \/| )   ( |
                | (_____ | |   | || |        | (_____ | (__    | (___) || (____)|| |      | (___) |
                (_____  )| |   | || |        (_____  )|  __)   |  ___  ||     __)| |      |  ___  |
                      ) || | /\| || |              ) || (      | (   ) || (\ (   | |      | (   ) |
                /\____) || (_\ \ || (____/\  /\____) || (____/\| )   ( || ) \ \__| (____/\| )   ( |
                \_______)(____\/_)(_______/  \_______)(_______/|/     \||/   \__/(_______/|/     \|                                                                   
                """;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(aciiArt);

            ProcessingOptions selectedProcessingChoice = (ProcessingOptions) 0;
            bool validInput = false;

            // Iterate until the proper input is selected by the user
            while (!validInput)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(string.Empty);
                Console.WriteLine("Select one of the options, by typing either 1 or 2:");
                Console.WriteLine("1) Create or re-create the Vector Database in SQL (runs Document Enrichment pipeline)");
                Console.WriteLine("2) Only answer the sample questions (runs Q&A over existing Vector Database pipeline)");
                Console.WriteLine("3) Only answer the sample questions with reasoning (runs Q&A over existing Vector Database pipeline)");
                var insertedText = Console.ReadLine();
                string trimmedInput = insertedText.Trim();

                if (trimmedInput == "1" || trimmedInput == "2" || trimmedInput == "3")
                {
                    validInput = true;
                    selectedProcessingChoice = (ProcessingOptions) Int32.Parse(trimmedInput);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Incorrect selection!!!!");
                }
            }

            Console.WriteLine("You selected: {0}", selectedProcessingChoice);

            ConfigurationBuilder configurationBuilder = new ConfigurationBuilder();
            IConfiguration configuration = configurationBuilder.AddUserSecrets<Program>().Build();
            string connectionString = configuration.GetSection("SQL")["SqlConnection"];
            var openAIAPIKey = configuration.GetSection("OpenAI")["APIKey"];
            var azureOpenAIAPIKey = configuration.GetSection("AzureOpenAI")["APIKey"];

            var builder = new HostBuilder();
            builder
                .ConfigureServices((hostContext, services) =>
                {
                    // with AddHttpClient we register the IHttpClientFactory
                    services.AddHttpClient();
                    // Retrieve Polly retry policy and apply it
                    var retryPolicy = Policies.HttpPolicies.GetRetryPolicy();
                    services.AddHttpClient<IOpenAIServiceManagement, OpenAIServiceManagement>().AddPolicyHandler(retryPolicy);
                });
            var host = builder.Build();

            // START the timer
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            var totalTokenLength = 0;
            var totalTextLength = 0;

            // CONFIG 
            // Instantiate new ML.NET Context
            // Note: MlContext is thread-safe
            var mlContext = new MLContext(100);
            // OpenAI Token Settings
            var MAXTOKENSPERLINE = 200;
            var MAXTOKENSPERPARAGRAPH = 840; // Provide enough context to answer questions
            var OVERLAPTOKENSPERPARAGRAPH = 40; // Overlap setting, could be set higher
            var MODELID = "gpt-3.5-turbo"; // "gpt-3.5-turbo" or "gpt-4" if you have access in OpenAI

            // UNCOMMENT IF USING AZURE OPENAI
            // Azure OpenAI (Azure not OpenAI)
            //var openAIClient = new OpenAIClient(
            //    new Uri("https://YOURAZUREOPENAIENDPOINT.openai.azure.com"), new Azure.AzureKeyCredential(azureOpenAIAPIKey));

            // OpenAI Client (not Azure OpenAI)
            // var openAIClient = new OpenAIClient(openAIAPIKey);

            // GET Current Environment Folder
            // Note: This will have the JSON documents from the checked-in code and overwrite each time run
            // Note: Can be used for offline mode in-stead of downloading (not to get your IP blocked), or use VPN
            var currentEnrichmentFolder = System.IO.Path.Combine(Environment.CurrentDirectory, "EnrichedDocuments");
            System.IO.Directory.CreateDirectory(currentEnrichmentFolder);

            // SET language to English
            StopWordsRemovingEstimator.Language language = StopWordsRemovingEstimator.Language.English;


            // SET the max degree of parallelism
            // Note: Default is to use 75% of the workstation or server cores.
            // Note: If cores are hyperthreaded, adjust accordingly (i.e. multiply *2)
            var isHyperThreaded = false;
            var executionDataFlowOptions = new ExecutionDataflowBlockOptions();
            executionDataFlowOptions.MaxDegreeOfParallelism =
                // Use 75% of the cores, if hyper-threading multiply cores *2
                Convert.ToInt32(Math.Ceiling((Environment.ProcessorCount * 0.75) *
                (isHyperThreaded ? 2: 1)));
            executionDataFlowOptions.MaxMessagesPerTask = 1;

            // SET the Data Flow Block Options
            // This controls the data flow from the Producer level
            // Note: This example is making web requests directly to Project Gutenberg
            // Note: If this is set to high, you may receive errors. In production, you would ensure request throughput
            var dataFlowBlockOptions = new DataflowBlockOptions {
                EnsureOrdered = true, // Ensures order, this is purely optional but messages will flow in order
                BoundedCapacity = 10,
                MaxMessagesPerTask = 10 };

            // SET the data flow pipeline options
            // Note: OPTIONAL Set MaxMessages to the number of books to process
            // Note: For example, setting MaxMessages to 2 will run only two books through the pipeline
            // Note: Set MaxMessages to 1 to process in sequence
            var dataFlowLinkOptions = new DataflowLinkOptions {
                PropagateCompletion = true,
                MaxMessages = -1
            };

            // TPL: BufferBlock - Seeds the queue with selected Project Gutenberg Books
            async Task ProduceGutenbergBooks(BufferBlock<EnrichedDocument> queue,
                IEnumerable<ProjectGutenbergBook> projectGutenbergBooks)
            {
                var currentSQLScriptsFolder = System.IO.Path.Combine(Environment.CurrentDirectory, "SQL");
                var sqlScriptsFilePath = Path.Combine(currentSQLScriptsFolder, "ProjectGutenbergScripts.sql");
                var scriptText = File.ReadAllText(sqlScriptsFilePath);

                // SQL scripts that are multi-command split by GO
                var sqlCommandsInScripts = scriptText.Split(new[] { "GO", "Go", "go" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var commandText in sqlCommandsInScripts)
                {
                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();

                        using (SqlCommand command = new SqlCommand(string.Empty, connection))
                        {
                            // This is just executing SQL scripts, so timeout should be relatively low
                            command.CommandTimeout = 100; // seconds
                            command.CommandText = commandText;
                            command.ExecuteNonQuery();
                        }

                        connection.Close();
                    }
                }

                foreach (var projectGutenbergBook in projectGutenbergBooks)
                {
                    // Add baseline information from the document book list
                    var enrichedDocument = new EnrichedDocument
                    {
                        BookTitle = projectGutenbergBook.BookTitle,
                        Author = projectGutenbergBook.Author,
                        Url = projectGutenbergBook.Url
                    };

                    await queue.SendAsync(enrichedDocument);
                }

                queue.Complete();
            }

            // TPL Block: Download the requested Gutenberg book resources as a text string
            var downloadBookText = new TransformBlock<EnrichedDocument, EnrichedDocument>(async enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Downloading: '{0}'", enrichedDocument.BookTitle);

                enrichedDocument.Text = await new HttpClient().GetStringAsync(enrichedDocument.Url);

                // Remove the beginning part of the Project Gutenberg info
                var indexOfBookBeginning = enrichedDocument.Text.IndexOf("GUTENBERG EBOOK") + "GUTENBERG EBOOK ".Length + enrichedDocument.BookTitle.Length;
                if (indexOfBookBeginning > 0)
                {
                    enrichedDocument.Text = enrichedDocument.Text.Substring(indexOfBookBeginning, enrichedDocument.Text.Length - indexOfBookBeginning);
                }

                if (indexOfBookBeginning < 0)
                {
                    indexOfBookBeginning = enrichedDocument.Text.IndexOf("GUTENBERG EBOOK") + "GUTENBERG EBOOK ".Length + enrichedDocument.BookTitle.Length;
                    if (indexOfBookBeginning > 0)
                    {
                        enrichedDocument.Text = enrichedDocument.Text.Substring(indexOfBookBeginning, enrichedDocument.Text.Length - indexOfBookBeginning);
                    }
                }

                // Remove the last part of the Project Gutenberg info to retrieve just the text
                var indexOfBookEnd = enrichedDocument.Text.IndexOf("*** END OF");
                if (indexOfBookEnd > 0)
                {
                    enrichedDocument.Text = enrichedDocument.Text.Substring(0, enrichedDocument.Text.IndexOf("*** END OF"));
                }

                if (indexOfBookEnd < 0)
                {
                    indexOfBookEnd = enrichedDocument.Text.IndexOf("***END OF");
                    if (indexOfBookEnd > 0)
                    {
                        enrichedDocument.Text = enrichedDocument.Text.Substring(0, enrichedDocument.Text.IndexOf("***END OF"));
                    }
                }

                return enrichedDocument;
            }, executionDataFlowOptions);

            // TPL Block: Download the requested Gutenberg book resources as a text string
            var chunkedLinesEnrichment = new TransformBlock<EnrichedDocument, EnrichedDocument>(enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Chunking text for: '{0}'", enrichedDocument.BookTitle);

                // Get the encoding for text-embedding-ada-002
                var cl100kBaseEncoding = GptEncoding.GetEncoding("cl100k_base");
                // Return the optimal text encodings, this is if tokens can be split perfect (no overlap)
                var encodedTokens = cl100kBaseEncoding.Encode(enrichedDocument.Text);

                enrichedDocument.TokenLength = encodedTokens.Count;
                totalTokenLength += enrichedDocument.TokenLength;

                return enrichedDocument;
            }, executionDataFlowOptions);

            // TPL Block: Performs text Machine Learning on the book texts
            var machineLearningEnrichment = new TransformBlock<EnrichedDocument, EnrichedDocument>(enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Machine Learning enrichment for: '{0}'", enrichedDocument.BookTitle);

                enrichedDocument.TextLength = enrichedDocument.Text.Length;
                totalTextLength += enrichedDocument.TextLength;

                var textData = new TextData { Text = enrichedDocument.Text };
                var textDataArray = new TextData[] { textData };

                IDataView emptyDataView = mlContext.Data.LoadFromEnumerable(new List<TextData>());
                EstimatorChain<StopWordsRemovingTransformer> textPipeline = mlContext.Transforms.Text
                    .NormalizeText("NormalizedText", "Text", caseMode: TextNormalizingEstimator.CaseMode.Lower, keepDiacritics: true, keepPunctuations: false, keepNumbers: false)
                    .Append(mlContext.Transforms.Text.TokenizeIntoWords("WordTokens", "NormalizedText", separators: new[] { ' ' }))
                    .Append(mlContext.Transforms.Text.RemoveDefaultStopWords("WordTokensRemovedStopWords", "WordTokens", language: language));
                TransformerChain<StopWordsRemovingTransformer> textTransformer = textPipeline.Fit(emptyDataView);
                PredictionEngine<TextData, TransformedTextData> predictionEngine = mlContext.Model.CreatePredictionEngine<TextData, TransformedTextData>(textTransformer);

                var prediction = predictionEngine.Predict(textData);

                // Set ML Enriched document variables
                enrichedDocument.NormalizedText = prediction.NormalizedText;
                enrichedDocument.WordTokens = prediction.WordTokens;
                enrichedDocument.WordTokensRemovedStopWords = prediction.WordTokensRemovedStopWords;

                // Calculate Stop Words
                var result = enrichedDocument.WordTokensRemovedStopWords.AsParallel()
                    .Where(word => word.Length > 3)
                    .GroupBy(x => x)
                    .OrderByDescending(x => x.Count())
                    .Select(x => new WordCount { WordName = x.Key, Count = x.Count() }).Take(100)
                    .ToList();

                enrichedDocument.TopWordCounts = result;

                // Calculate the Paragraphs based on TokenCount
                var enrichedDocumentLines = Microsoft.SemanticKernel.Text.TextChunker.SplitPlainTextLines(enrichedDocument.Text, MAXTOKENSPERLINE);
                enrichedDocument.Paragraphs = Microsoft.SemanticKernel.Text.TextChunker.SplitPlainTextParagraphs(enrichedDocumentLines, MAXTOKENSPERPARAGRAPH, overlapTokens: OVERLAPTOKENSPERPARAGRAPH);

                return enrichedDocument;
            }, executionDataFlowOptions);

            // TPL Block: Get OpenAI Embeddings
            var retrieveEmbeddings = new TransformBlock<EnrichedDocument, EnrichedDocument>(async enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("OpenAI Embeddings for: '{0}'", enrichedDocument.BookTitle);

                foreach (var paragraph in enrichedDocument.Paragraphs)
                {
                    // Create the OpenAI Service
                    var openAIService = host.Services.GetRequiredService<IOpenAIServiceManagement>();
                    openAIService.APIKey = openAIAPIKey;
                    var embeddings = await openAIService.GetEmbeddings(paragraph);
                    enrichedDocument.ParagraphEmbeddings.Add(embeddings);
                }

                return enrichedDocument;

            }, executionDataFlowOptions);

            // TPL Block: Persist the document embeddings and the final enriched document to Json
            var persistToDatabaseAndJson = new TransformBlock<EnrichedDocument, EnrichedDocument>(enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Persisting to DB & converting to JSON file: '{0}'", enrichedDocument.BookTitle);

                // 1 - Save to SQL Server
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    for (int i = 0; i != enrichedDocument.Paragraphs.Count; i++)
                    {

                        // Insert into ProjectGutenbergBooks
                        var identityId = 0;
                        using (SqlCommand command = new SqlCommand(string.Empty, connection))
                        {
                            command.CommandText = "INSERT INTO ProjectGutenbergBooks(Author, BookTitle, ParagraphEmbeddings, ParagraphEmbeddingsCosineSimilarityDenominator) VALUES(@author, @bookTitle, @paragraphEmbeddings, @paragraphEmbeddingsCosineSimilarityDenominator); SELECT SCOPE_IDENTITY()";
                            command.Parameters.AddWithValue("@author", enrichedDocument.Author);
                            command.Parameters.AddWithValue("@bookTitle", enrichedDocument.BookTitle);
                            //command.Parameters.AddWithValue("@url", enrichedDocument.Url);
                            //command.Parameters.AddWithValue("@paragraph", enrichedDocument.Paragraphs[i]);
                            var paragraphEmbeddings = enrichedDocument.ParagraphEmbeddings[i];
                            var jsonStringParagraphEmbeddings = JsonConvert.SerializeObject(paragraphEmbeddings);
                            command.Parameters.AddWithValue("@paragraphEmbeddings", jsonStringParagraphEmbeddings);
                            var multipliedLists = from a in paragraphEmbeddings select (a*a);
                            var paragraphEmbeddingsCosineSimilarityDenominator = Math.Sqrt(multipliedLists.Sum());
                            command.Parameters.AddWithValue("@paragraphEmbeddingsCosineSimilarityDenominator", paragraphEmbeddingsCosineSimilarityDenominator);
                            command.CommandTimeout = 2000;

                            identityId = Convert.ToInt32(command.ExecuteScalar());
                        }

                        // Insert into ProjectGutenbergBookDetails
                        using (SqlCommand command = new SqlCommand(string.Empty, connection))
                        {
                            command.CommandText = "INSERT INTO ProjectGutenbergBookDetails(Id, Url, Paragraph) VALUES(@Id, @url, @paragraph)";
                            command.Parameters.AddWithValue("@Id", identityId);
                            command.Parameters.AddWithValue("@url", enrichedDocument.Url);
                            command.Parameters.AddWithValue("@paragraph", enrichedDocument.Paragraphs[i]);
                            command.CommandTimeout = 2000;
                            command.ExecuteNonQuery();
                        }
                    }

                    connection.Close();
                }

                // 2 - Write out JSON file
                // Convert to JSON String (all the public properties)
                var jsonString = JsonConvert.SerializeObject(enrichedDocument);

                // Create the book file path
                var jsonBookFilePath = Path.Combine(currentEnrichmentFolder, enrichedDocument.JsonFileName);

                // Write out JSON string to local storage
                using (StreamWriter outputFile = new StreamWriter(jsonBookFilePath))
                {
                    outputFile.WriteLine(jsonString);
                }

                return enrichedDocument;
            });

            // TPL Block: Prints out final information of enriched document
            var printEnrichedDocument = new ActionBlock<EnrichedDocument>(enrichedDocument =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Completed \"{0}\" | Text Size: {1} | Word Count: {2} | Word Count Removed Stop Words: {3}",
                   enrichedDocument.ID,
                   enrichedDocument.Text.Length.ToString(),
                   enrichedDocument.WordTokens.Length.ToString(),
                   enrichedDocument.WordTokensRemovedStopWords.Length.ToString());

                Console.ForegroundColor = ConsoleColor.Green;
            });

            // TPL Block: Persist the document embeddings and the final enriched document to Json
            var createVectorIndex = new ActionBlock<string>(stringMessage =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Creating Project Gutenberg Books Index...");

                // 1 - Save to SQL Server
                var currentSQLScriptsFolder = System.IO.Path.Combine(Environment.CurrentDirectory, "SQL");
                var sqlScriptsFilePath = Path.Combine(currentSQLScriptsFolder, "spCreateProjectGutenbergVectorsIndex.sql");
                var scriptText = File.ReadAllText(sqlScriptsFilePath);

                // Execute script to create database objects (tables, stored procedures)
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    // SQL scripts that are multi-command split by GO
                    var sqlCommandsInScript = scriptText.Split(new[] { "GO", "Go", "go" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var commandText in sqlCommandsInScript)
                    {
                        using (SqlCommand command = new SqlCommand(string.Empty, connection))
                        {
                            // Increase the command timeout to ensure enough time for processing on slow servers
                            command.CommandTimeout = 1000; // seconds
                            command.CommandText = commandText;
                            command.ExecuteNonQuery();
                        }
                    }

                    connection.Close();
                }
            });

            // TPL Block: Search Vector Index
            var retrieveEmbeddingsForSearch = new TransformBlock<SearchMessage, SearchMessage>(async searchMessage =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Retrieving OpenAI Embeddings for the phrase: '{0}'", searchMessage.SearchString);


                // Create the OpenAI Service
                var openAIService = host.Services.GetRequiredService<IOpenAIServiceManagement>();
                openAIService.APIKey = openAIAPIKey;
                var embeddings = await openAIService.GetEmbeddings(searchMessage.SearchString);
                searchMessage.EmbeddingsJsonString = System.Text.Json.JsonSerializer.Serialize(embeddings);

                return searchMessage;
            });

            // TPL Block: Search Vector Index
            var searchVectorIndex = new TransformBlock<SearchMessage, SearchMessage>(searchMessage =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Searching Project Gutenberg SQL Vector (Database) Index for '{0}'", searchMessage.SearchString);

                var dataSet = new DataSet();

                // Execute script to create database objects (tables, stored procedures)
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(string.Empty, connection))
                    {
                        command.CommandText = "spSearchProjectGutenbergVectors";
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@jsonOpenAIEmbeddings", searchMessage.EmbeddingsJsonString);
                        command.Parameters.AddWithValue("@bookTitle", searchMessage.BookTitle);

                        var sqlAdapter = new SqlDataAdapter(command);
                        sqlAdapter.Fill(dataSet);
                    }

                    connection.Close();
                }

                var paragraphResults = dataSet.Tables[0].AsEnumerable()
                    .Select(dataRow => new ParagraphResults
                    {
                        Id = dataRow.Field<int>("Id"),
                        BookTitle = dataRow.Field<string>("BookTitle"),
                        Author = dataRow.Field<string>("Author"),
                        CosineDistance = dataRow.Field<double>("CosineDistance"),
                        Paragraph = dataRow.Field<string>("Paragraph")
                    }).ToList();

                searchMessage.TopParagraphSearchResults = paragraphResults;

                return searchMessage;

                //Console.ForegroundColor = ConsoleColor.Magenta;
                //Console.WriteLine("Top paragraph for search question: {0}. Cosine Distance {1} - {2}", searchMessage.SearchString, searchMessage.TopParagraphSearchResults[0].CosineDistance,
                //    searchMessage.TopParagraphSearchResults[0].Paragraph);
            });

            var answerQuestionWithOpenAI = new ActionBlock<SearchMessage>(async searchMessage =>
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Answering Question using OpenAI for '{0}'", searchMessage.SearchString);

                var semanticKernel = Kernel.Builder
                    // You can use the chat completion service (use GPT 3.5 Turbo or GPT-4)
                    .WithOpenAIChatCompletionService(modelId: MODELID, apiKey: openAIAPIKey)
                    .Build();

                var pluginsDirectory = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "SemanticKernelPlugins");
                var bookPlugin = semanticKernel.ImportSemanticSkillFromDirectory(pluginsDirectory, "BookPlugin");

                var questionContext = new ContextVariables();
                questionContext.Set("SEARCHSTRING", searchMessage.SearchString);
                questionContext.Set("PARAGRAPH", searchMessage.TopParagraphSearchResults[1].Paragraph);
                questionContext.Set("REASONING", (selectedProcessingChoice == ProcessingOptions.OnlyPerformQuestionAndAnswer)? string.Empty : "Provide detailed reasoning how you arrived at the answer. Provide a score from 1 to 10 on how confident you are on this answer.");

                var answerBookQuestion = await semanticKernel.RunAsync(questionContext, bookPlugin[searchMessage.SemanticKernelPluginName]);

                //// Manual method of registering SK functions
                //string answerQuestionContext = """
                //    Answer the following question based on the context paragraph below: 
                //    ---Begin Question---
                //    {{$SEARCHSTRING}}
                //    ---End Question---
                //    ---Begin Paragraph---
                //    {{$PARAGRAPH}}
                //    ---End Paragraph---
                //    """;

                //var questionPromptConfig = new PromptTemplateConfig
                //{
                //    Description = "Search & Answer",
                //    Completion =
                //        {
                //            MaxTokens = 500,
                //            Temperature = 0.7,
                //            TopP = 0.6,
                //        }
                //};

                //var myPromptTemplate = new PromptTemplate(
                //    answerQuestionContext,
                //    questionPromptConfig,
                //    semanticKernel
                //);

                //var myFunctionConfig = new SemanticFunctionConfig(questionPromptConfig, myPromptTemplate);
                //var answerFunction = semanticKernel.RegisterSemanticFunction(
                //    "VectorSearchAndAnswer",
                //    "AnswerFromQuestion",
                //    myFunctionConfig);

                //var openAIQuestionAnswer = await semanticKernel.RunAsync(questionContext, answerFunction);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("For the question: '{0}'\nBased on the paragraph found from the vector index search using the '{2}' Semantic Kernel plugin with OpenAI, the answer is:\n'{1}'", searchMessage.SearchString, answerBookQuestion.Result, searchMessage.SemanticKernelPluginName);
                Console.WriteLine(string.Empty);
            });

            // TPL: BufferBlock - Seeds the queue with selected Project Gutenberg Books
            async Task ProduceGutenbergBooksSearches(BufferBlock<SearchMessage> searchQueue,
                IEnumerable<SearchMessage> searchMessages)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Creating Book Search...");

                foreach (var searchMessage in searchMessages)
                {
                    await searchQueue.SendAsync(searchMessage);
                }

                searchQueue.Complete();
            }

            // TPL Pipeline: Build the pipeline workflow graph from the TPL Blocks
            var enrichmentPipeline = new BufferBlock<EnrichedDocument>(dataFlowBlockOptions);
            enrichmentPipeline.LinkTo(downloadBookText, dataFlowLinkOptions);
            downloadBookText.LinkTo(chunkedLinesEnrichment, dataFlowLinkOptions);
            chunkedLinesEnrichment.LinkTo(machineLearningEnrichment, dataFlowLinkOptions);
            machineLearningEnrichment.LinkTo(retrieveEmbeddings, dataFlowLinkOptions);
            retrieveEmbeddings.LinkTo(persistToDatabaseAndJson, dataFlowLinkOptions);
            persistToDatabaseAndJson.LinkTo(printEnrichedDocument, dataFlowLinkOptions);

            // Only seed the initial pipeline if selected to process
            if (selectedProcessingChoice == ProcessingOptions.RunFullDataEnrichmentPipeline)
            {
                // TPL: Start the producer by feeding it a list of books
                var enrichmentProducer = ProduceGutenbergBooks(enrichmentPipeline, ProjectGutenbergBookService.GetBooks());

                // TPL: Since this is an asynchronous Task process, wait for the producer to finish putting all the messages on the queue
                // Works when queue is limited and not a "forever" queue
                await Task.WhenAll(enrichmentProducer);
                // TPL: Wait for the last block in the pipeline to process all messages.
                printEnrichedDocument.Completion.Wait();

                // Create the Vectors Index
                var accepted = await createVectorIndex.SendAsync(string.Empty);
                createVectorIndex.Complete();
                createVectorIndex.Completion.Wait();
            }


            // Search the Vectors Index, then answer the question using Semantic Kernel
            var searchMessagePipeline = new BufferBlock<SearchMessage>(dataFlowBlockOptions);
            searchMessagePipeline.LinkTo(retrieveEmbeddingsForSearch, dataFlowLinkOptions);
            retrieveEmbeddingsForSearch.LinkTo(searchVectorIndex, dataFlowLinkOptions);
            searchVectorIndex.LinkTo(answerQuestionWithOpenAI, dataFlowLinkOptions);

            // TPL: Start the producer by feeding it a list of books
            var bookSearchesProducer = ProduceGutenbergBooksSearches(searchMessagePipeline, ProjectGutenbergBookService.GetQueries());
            await Task.WhenAll(bookSearchesProducer);
            // TPL: Wait for the last block in the pipeline to process all messages.
            answerQuestionWithOpenAI.Completion.Wait();


            stopwatch.Stop();
            // Print out duration of work
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.Empty);
            Console.WriteLine("Job Completed In:  {0} seconds", + stopwatch.Elapsed.TotalSeconds);
            // Only print job metrics if job was selected to run
            if (selectedProcessingChoice == ProcessingOptions.RunFullDataEnrichmentPipeline)
            {
                Console.WriteLine("Total Text OpenAI Tokens Processed: " + totalTokenLength.ToString("N0"));
                Console.WriteLine("Total Text Characters Processed:    " + totalTextLength.ToString("N0"));
            }
        }
    }
}
