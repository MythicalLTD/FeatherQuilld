# bash completion for featherquilld / quilld

_featherquilld()
{
    local cur prev words cword
    if declare -F _init_completion >/dev/null 2>&1; then
        _init_completion || return
    else
        COMPREPLY=()
        cur="${COMP_WORDS[COMP_CWORD]}"
        prev="${COMP_WORDS[COMP_CWORD-1]}"
        words=("${COMP_WORDS[@]}")
        cword="${COMP_CWORD}"
    fi

    local commands="configure help version"
    local global_opts="-h --help -v --version -c --config"
    local configure_opts="
        --join-data --panel-url --callback-host --allow-insecure --keep-oauth-key
        --node-name --node-fqdn --location-id --daemon-listen --sftp-port --daemon-base
        --install-service --no-service --override --quiet -q -c --config
        -h --help
    "

    local cmd=""
    local i
    for ((i = 1; i < cword; i++)); do
        case "${words[i]}" in
            configure|help|version)
                cmd="${words[i]}"
                break
                ;;
            -c|--config)
                ((i++))
                ;;
        esac
    done

    case "${prev}" in
        -c|--config)
            COMPREPLY=($(compgen -f -- "${cur}"))
            return
            ;;
        --join-data|--panel-url|--callback-host|--node-name|--node-fqdn|--location-id|--daemon-listen|--sftp-port|--daemon-base)
            COMPREPLY=()
            return
            ;;
    esac

    if [[ -z "${cmd}" ]]; then
        if [[ "${cur}" == -* ]]; then
            COMPREPLY=($(compgen -W "${global_opts}" -- "${cur}"))
        else
            COMPREPLY=($(compgen -W "${commands} ${global_opts}" -- "${cur}"))
        fi
        return
    fi

    if [[ "${cmd}" == configure && "${cur}" == -* ]]; then
        COMPREPLY=($(compgen -W "${configure_opts}" -- "${cur}"))
        return
    fi
}

complete -F _featherquilld featherquilld quilld
