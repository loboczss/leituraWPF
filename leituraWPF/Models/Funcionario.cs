namespace leituraWPF.Models
{
    /// <summary>
    /// Representa um funcionário obtido da lista do SharePoint ou do cache local.
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
