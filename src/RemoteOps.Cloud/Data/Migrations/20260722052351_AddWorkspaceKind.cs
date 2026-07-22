using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteOps.Cloud.Data.Migrations
{
    /// <summary>
    /// A marca de nascimento do workspace (pessoal × time). ADITIVA: uma coluna nova, nada é
    /// renomeado, movido ou apagado — nenhuma linha existente muda de valor em nenhum outro campo.
    ///
    /// <para><b>O <c>defaultValue</c> é a decisão que importa aqui.</b> Ele preenche TODA linha que
    /// já está no banco, inclusive o workspace do operador com os ~700 clientes dele. Escolhido
    /// <c>personal</c> por dois motivos, nessa ordem:</para>
    ///
    /// <list type="number">
    /// <item><b>Erra para o lado seguro.</b> Marcar um cofre pessoal como "time" por engano
    /// autorizaria convite nele — e o <c>/sync</c> é escopado por workspace, então o convidado
    /// baixaria o acervo inteiro do dono. Marcar um time como "pessoal" por engano custa uma recusa
    /// explicada na tela, e o operador refaz o time.</item>
    /// <item><b>É o valor CORRETO.</b> Até esta versão, o único caminho que criava workspace era o
    /// <c>/auth/register</c> (o <c>POST /workspaces</c> nasce nesta mesma entrega, ainda não
    /// publicada). Todo workspace que existe hoje é, de fato, o cofre pessoal de alguém.</item>
    /// </list>
    ///
    /// <para><b>Sem backfill por heurística</b> — em especial, sem "membership com <c>WrappedWk</c>
    /// ⇒ é time". Era exatamente o botão de convite defeituoso que gravava chave de time na
    /// membership do workspace PESSOAL; uma heurística assim promoveria a time justamente o cofre
    /// que esta coluna existe para proteger.</para>
    /// </summary>
    public partial class AddWorkspaceKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "workspaces",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "personal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "workspaces");
        }
    }
}
