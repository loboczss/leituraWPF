namespace leituraWPF.Models
{
    /// <summary>
    /// Representa um funcionário conforme armazenado no arquivo funcionarios.csv.
    /// </summary>
    public record Funcionario(
        string Matricula,
        string Nome,
        string Funcao,
        string Escala,
        string Departamento,
        string Cidade,
        string DataAdmissao);
}
