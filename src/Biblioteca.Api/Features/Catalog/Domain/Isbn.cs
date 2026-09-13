namespace Biblioteca.Api.Features.Catalog.Domain;

/// <summary>
/// Value object do ISBN: normaliza a entrada (remove hífen/espaço, "X" maiúsculo) e
/// valida o dígito verificador de ISBN-10 ou ISBN-13. Duas grafias do mesmo livro —
/// "978-85-359-0277-5" e "9788535902775" — resultam no mesmo valor normalizado.
/// </summary>
public sealed record Isbn
{
    public string Value { get; }

    private Isbn(string value) => Value = value;

    /// <exception cref="FormatException">
    /// <paramref name="input"/> não é um ISBN-10 nem ISBN-13 com dígito verificador válido.
    /// </exception>
    public static Isbn Parse(string input)
    {
        if (!TryParse(input, out var isbn))
        {
            throw new FormatException($"'{input}' não é um ISBN-10 ou ISBN-13 válido.");
        }

        return isbn;
    }

    public static bool TryParse(string? input, out Isbn isbn)
    {
        isbn = Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = Normalize(input);

        var isValid = normalized.Length switch
        {
            10 => IsValidIsbn10(normalized),
            13 => IsValidIsbn13(normalized),
            _ => false,
        };

        if (!isValid)
        {
            return false;
        }

        isbn = new Isbn(normalized);
        return true;
    }

    private static readonly Isbn Empty = new(string.Empty);

    private static string Normalize(string input)
    {
        Span<char> buffer = stackalloc char[input.Length];
        var count = 0;

        foreach (var c in input)
        {
            if (char.IsDigit(c))
            {
                buffer[count++] = c;
            }
            else if (char.ToUpperInvariant(c) == 'X')
            {
                buffer[count++] = 'X';
            }
            // demais caracteres (hífen, espaço, etc.) são descartados
        }

        return new string(buffer[..count]);
    }

    private static bool IsValidIsbn10(string digits)
    {
        var sum = 0;

        for (var i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(digits[i]))
            {
                return false;
            }

            sum += (digits[i] - '0') * (10 - i);
        }

        var checkChar = digits[9];
        var checkValue = checkChar == 'X' ? 10 : checkChar - '0';

        if (checkChar != 'X' && !char.IsAsciiDigit(checkChar))
        {
            return false;
        }

        sum += checkValue;
        return sum % 11 == 0;
    }

    private static bool IsValidIsbn13(string digits)
    {
        var sum = 0;

        for (var i = 0; i < 13; i++)
        {
            if (!char.IsAsciiDigit(digits[i]))
            {
                return false;
            }

            var digit = digits[i] - '0';
            sum += i % 2 == 0 ? digit : digit * 3;
        }

        return sum % 10 == 0;
    }

    public override string ToString() => Value;
}
