using UglyToad.PdfPig;

namespace AlphaPDF.Services
{
    internal sealed class SearchResult
    {
        public Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> PageRects { get; } = [];
        public List<int> ResultPages { get; } = [];
        public int TotalHits { get; set; }
    }

    internal sealed class SearchService
    {
        /// <summary>
        /// Scans every page of <paramref name="filePath"/> for <paramref name="query"/> (case-insensitive).
        /// Returns an empty result when query is blank or the file cannot be opened.
        /// </summary>
        public SearchResult Search(string filePath, string query)
        {
            var result = new SearchResult();
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath))
                return result;

            string lowerQuery = query.ToLowerInvariant();

            try
            {
                using var doc = PdfDocument.Open(filePath);
                for (int pi = 0; pi < doc.NumberOfPages; pi++)
                {
                    var page = doc.GetPage(pi + 1);
                    var hits = FindMatchesOnPage(page, lowerQuery);
                    if (hits.Count > 0)
                    {
                        result.PageRects[pi] = hits;
                        result.ResultPages.Add(pi);
                        result.TotalHits += hits.Count;
                    }
                }
            }
            catch { /* return whatever was collected so far */ }

            return result;
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindMatchesOnPage(
    UglyToad.PdfPig.Content.Page page, string lowerQuery)
        {
            var result = new List<(double, double, double, double)>();
            var words = page.GetWords().ToList();

            for (int i = 0; i < words.Count; i++)
            {
                // 1. Khớp từ đơn (Single word match)
                if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
                {
                    var bb = words[i].BoundingBox;
                    double safeH = words[i].Letters.Count > 0
                        ? words[i].Letters.Max(l => l.PointSize)
                        : bb.Height;
                    double safeB = bb.Top - safeH;

                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                // 2. Khớp cụm từ (Multi-word match)
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
                {
                    combined += " " + words[j].Text;

                    // Dùng IndexOf thay vì Contains để biết chính xác vị trí khớp
                    int matchIndex = combined.ToLowerInvariant().IndexOf(lowerQuery);
                    if (matchIndex >= 0)
                    {
                        // Chỉ chấp nhận nếu phần khớp có dính dáng đến từ BẮT ĐẦU (words[i]).
                        // Nếu matchIndex >= độ dài words[i], nghĩa là từ khóa nằm tuốt ở các từ phía sau.
                        if (matchIndex < words[i].Text.Length)
                        {
                            double minX = double.MaxValue, minY = double.MaxValue;
                            double maxX = double.MinValue, maxY = double.MinValue;

                            for (int k = i; k <= j; k++)
                            {
                                var wbb = words[k].BoundingBox;
                                double safeH = words[k].Letters.Count > 0
                                    ? words[k].Letters.Max(l => l.PointSize)
                                    : wbb.Height;
                                double safeB = wbb.Top - safeH;

                                minX = Math.Min(minX, wbb.Left);
                                minY = Math.Min(minY, wbb.Bottom);
                                maxX = Math.Max(maxX, wbb.Right);
                                maxY = Math.Max(maxY, wbb.Top);
                            }
                            result.Add((minX, minY, maxX, maxY));

                            // [QUAN TRỌNG] Nhảy cóc biến i qua các từ đã được gộp để tránh quét lại gây đè highlight
                            i = j;
                        }

                        // Vì combined đã bao hàm từ khóa, nối thêm từ cũng vô ích. Ngắt inner loop.
                        break;
                    }
                }
            }
            return result;
        }
    }
}
