using LibationEngine.Contracts;

namespace LibationEngine;

public interface IEngineData
{
	IReadOnlyList<AccountDto> GetAccounts();
	LibraryPageDto GetLibrary(int offset, int limit);
	BookDto? GetBook(string asin);
}
