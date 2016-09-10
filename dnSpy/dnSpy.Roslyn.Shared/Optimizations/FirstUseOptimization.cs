﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

// Calls some code in a background thread so initialization code gets a chance
// to run, eg. Roslyn MEF code, jitting methods, etc.
// The code editor benefits from this optimization since a lot of Roslyn code
// gets called and initialized the first time its window is shown.
// About 20-25MB extra memory usage in my litle test but code editor starts a lot quicker.
//
// The following gets initialized:
//	- RoslynMefHostServices.DefaultServices
//	- Workspaces code needed by RoslynLanguageCompiler
//	- Classification code, used indirectly by RoslynLanguageCompiler
//	- Completion code

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Utilities;
using dnSpy.Roslyn.Shared.Intellisense.Completions;
using dnSpy.Roslyn.Shared.Text;
using dnSpy.Roslyn.Shared.Text.Tagging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace dnSpy.Roslyn.Shared.Optimizations {
	[ExportAutoLoaded]
	sealed class FirstUseOptimizationLoader : IAutoLoaded {
		[ImportingConstructor]
		FirstUseOptimizationLoader(IThemeClassificationTypeService themeClassificationTypeService, ITextBufferFactoryService textBufferFactoryService) {
			new FirstUseOptimization(themeClassificationTypeService, textBufferFactoryService);
		}
	}

	sealed class FirstUseOptimization {
		static readonly string csharpCode = @"
sealed class C {
	int Method() {
		return 42;
	}
}
";
		static readonly string visualBasicCode = @"
Module Module1
	Sub Method()
		Dim local As Integer = 42
	End Sub
End Module
";

		public FirstUseOptimization(IThemeClassificationTypeService themeClassificationTypeService, ITextBufferFactoryService textBufferFactoryService) {
			var buffer = textBufferFactoryService.CreateTextBuffer();
			var tagger = new RoslynTagger(themeClassificationTypeService);
			Task.Run(() => InitializeAsync(buffer, tagger))
			.ContinueWith(t => {
				var ex = t.Exception;
				Debug.Assert(ex == null);
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
		}

		async Task InitializeAsync(ITextBuffer buffer, ITagger<IClassificationTag> tagger) {
			ProfileOptimizationHelper.StartProfile("startup-roslyn");
			var refs = new MetadataReference[] {
				MetadataReference.CreateFromFile(typeof(int).Assembly.Location),
			};
			await InitializeAsync(buffer, csharpCode, refs, LanguageNames.CSharp, tagger, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true), new CSharpParseOptions());
			await InitializeAsync(buffer, visualBasicCode, refs, LanguageNames.VisualBasic, tagger, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary), new VisualBasicParseOptions());
			(tagger as IDisposable)?.Dispose();
		}

		async Task InitializeAsync(ITextBuffer buffer, string code, MetadataReference[] refs, string languageName, ITagger<IClassificationTag> tagger, CompilationOptions compilationOptions, ParseOptions parseOptions) {
			using (var workspace = new AdhocWorkspace(RoslynMefHostServices.DefaultServices)) {
				var documents = new List<DocumentInfo>();
				var projectId = ProjectId.CreateNewId();
				documents.Add(DocumentInfo.Create(DocumentId.CreateNewId(projectId), "main.cs", null, SourceCodeKind.Regular, TextLoader.From(buffer.AsTextContainer(), VersionStamp.Default)));

				var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, "compilecodeproj", Guid.NewGuid().ToString(), languageName,
					compilationOptions: compilationOptions
							.WithOptimizationLevel(OptimizationLevel.Release)
							.WithPlatform(Platform.AnyCpu)
							.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default),
					parseOptions: parseOptions,
					documents: documents,
					metadataReferences: refs,
					isSubmission: false, hostObjectType: null);
				workspace.AddProject(projectInfo);
				foreach (var doc in documents)
					workspace.OpenDocument(doc.Id);

				buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), code);

				// Initialize classification code paths
				var spans = new NormalizedSnapshotSpanCollection(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length));
				foreach (var tagSpan in tagger.GetTags(spans)) { }

				// Initialize completion code paths
				var info = CompletionInfo.Create(buffer.CurrentSnapshot);
				Debug.Assert(info != null);
				if (info != null) {
					var completionTrigger = CompletionTrigger.Default;
					var completionList = await info.Value.CompletionService.GetCompletionsAsync(info.Value.Document, 0, completionTrigger);
				}
			}
		}
	}
}
